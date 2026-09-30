using System;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// Checks a built mod assembly against the mod loader's namespace blacklist,
/// before the game gets a chance to reject it.
///
/// Besiege scans every mod assembly and refuses to load one that references a
/// forbidden member, e.g.
///
///     [Security] You are not allowed to use
///     System.String System.Reflection.MemberInfo::get_Name()
///     [Security] Not loading .../ClippyScripts.dll
///
/// which is easy to trip by accident -- `e.GetType().Name` is enough, because
/// Type.Name is declared on System.Reflection.MemberInfo. Compiling cleanly says
/// nothing about passing this, so it is worth checking at build time.
///
/// Calling the game's own AssemblyScanner would be ideal, and it is public, but
/// it segfaults outside a Unity player. So the rules below are reproduced from
/// docs/MODDING-NOTES.md instead. That means this can drift from the real thing:
/// it is a fast first line of defence, not the authority.
/// </summary>
static class BlacklistCheck
{
    /// <summary>Namespace prefixes the loader refuses, with their carve-outs.</summary>
    /// Copied verbatim out of InternalModding.Assemblies.AssemblyScanner's static
    /// constructor, so this check matches the game rather than approximating it.
    /// The scanner tests <c>(namespace + "." + typeName).StartsWith(prefix)</c>,
    /// which is why "UnityEngine.WWW" catches UnityEngine.WWWForm as well but
    /// leaves UnityEngine.Networking.UnityWebRequest alone.
    static readonly string[] Blacklist =
    {
        "System.IO", "System.Net", "System.Xml", "System.Reflection",
        "System.Runtime.InteropServices", "System.Diagnostics", "System.Security",
        "Mono.CSharp", "Mono.Cecil", "System.CodeDom.Compiler", "CSharpCompiler",
        "IKVM", "Microsoft", "Mono.CompilerServices", "UnityEngine.WWW",
        "UnityEngine.MasterServer", "PlayFab", "Steamworks", "GameGrind",
        "InternalModding", "BesiegeDlc",
    };

    /// <summary>
    /// The scanner's carve-outs, matched as whole type names against
    /// <c>namespace + "." + typeName</c>. Note what is *not* here: StringReader,
    /// StringWriter, File and Directory are all System.IO and all refused, and
    /// the "System.Security.Cryptography" entry only exempts a type by that exact
    /// name -- individual cipher classes underneath it are still forbidden.
    /// </summary>
    static readonly string[] Whitelist =
    {
        "System.IO.Stream", "System.IO.TextWriter", "System.IO.TextReader",
        "System.IO.BinaryWriter", "System.IO.BinaryReader", "System.IO.MemoryStream",
        "System.IO.Path", "System.IO.SeekOrigin",
        "System.Diagnostics.Stopwatch", "System.Security.Cryptography",
        "Mono.CSharp.Tuple`2", "Mono.CSharp.Tuple`3",
    };

    /// <summary>Individually forbidden methods, as namespace.type.method.</summary>
    static readonly string[] ForbiddenMethods =
    {
        "XmlSaver.Save", "LevelXMLSaver.Create",
        "UnityEngine.AssetBundle.LoadFromFile",
        "UnityEngine.AssetBundle.LoadFromFileAsync",
    };

    public static int Main(string[] args)
    {
        string target = args[0];
        for (int i = 1; i < args.Length; i++)
        {
            try { Assembly.LoadFrom(args[i]); } catch (Exception) { }
        }

        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(target);
        }
        catch (Exception e)
        {
            Console.WriteLine("BLACKLIST: could not load " + target + ": " + e.Message);
            return 2;
        }

        List<string> violations = new List<string>();
        int methods = 0;

        foreach (Type type in asm.GetTypes())
        {
            MethodBase[] all;
            try { all = Members(type); } catch (Exception) { continue; }

            foreach (MethodBase method in all)
            {
                byte[] il = ILOf(method);
                if (il == null) { continue; }
                methods++;
                try
                {
                    Inspect(type, method, il, violations);
                }
                catch (Exception e)
                {
                    // Decoding is best-effort. Say so rather than dying, so one
                    // odd method body cannot hide every other finding.
                    Console.WriteLine("  (could not decode " + type.FullName + "::" +
                                      method.Name + ": " + e.Message + ")");
                }
            }
        }

        Console.WriteLine("Blacklist check: " + methods + " method bodies scanned.");
        if (violations.Count == 0)
        {
            Console.WriteLine("  OK - nothing the mod loader forbids.");
            return 0;
        }

        Console.WriteLine("  " + violations.Count + " FORBIDDEN REFERENCE(S):");
        foreach (string v in violations)
        {
            Console.WriteLine("    " + v);
        }
        Console.WriteLine();
        Console.WriteLine("  Besiege will refuse to load this assembly. See docs/MODDING-NOTES.md.");
        return 1;
    }

    static MethodBase[] Members(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
        List<MethodBase> list = new List<MethodBase>();
        list.AddRange(type.GetMethods(Flags));
        list.AddRange(type.GetConstructors(Flags));
        return list.ToArray();
    }

    static byte[] ILOf(MethodBase method)
    {
        try
        {
            MethodBody body = method.GetMethodBody();
            return body == null ? null : body.GetILAsByteArray();
        }
        catch (Exception) { return null; }
    }

    static void Inspect(Type owner, MethodBase method, byte[] il, List<string> violations)
    {
        Module module = method.Module;
        int i = 0;
        while (i < il.Length)
        {
            int op = il[i++];
            if (op == 0xFE) { if (i >= il.Length) { break; } op = 0xFE00 | il[i++]; }

            int size = OperandSize(op);
            if (size == -1)
            {
                if (i + 4 > il.Length) { break; }
                int n = BitConverter.ToInt32(il, i);
                i += 4 + 4 * n;
                continue;
            }

            if (TakesMemberToken(op))
            {
                if (i + 4 > il.Length) { break; }
                int token = BitConverter.ToInt32(il, i);
                int table = (token >> 24) & 0xFF;
                // Only tables that resolve safely; a bad guess aborts the runtime
                // in native code rather than throwing something catchable.
                if (table == 0x06 || table == 0x0A || table == 0x04 || table == 0x2B)
                {
                    Check(owner, method, module, token, violations);
                }
            }
            // A mis-sized operand would walk off the end; stop rather than throw.
            if (i + size > il.Length) { break; }
            i += size;
        }
    }

    static void Check(Type owner, MethodBase method, Module module, int token,
                      List<string> violations)
    {
        Type declaring;
        string memberName;
        try
        {
            MemberInfo member = module.ResolveMember(token);
            declaring = member.DeclaringType;
            memberName = member.Name;
        }
        catch (Exception) { return; }

        if (declaring == null) { return; }
        string full = declaring.FullName;
        if (full == null) { return; }

        if (!IsForbidden(full) && !IsForbiddenMethod(full, memberName)) { return; }

        string where = owner.FullName + "::" + method.Name;
        string what = full + "::" + memberName + "   (in " + where + ")";
        if (!violations.Contains(what))
        {
            violations.Add(what);
        }
    }

    static bool IsForbidden(string full)
    {
        bool listed = false;
        for (int i = 0; i < Blacklist.Length; i++)
        {
            if (full.StartsWith(Blacklist[i], StringComparison.Ordinal)) { listed = true; break; }
        }
        if (!listed) { return false; }

        for (int i = 0; i < Whitelist.Length; i++)
        {
            if (full == Whitelist[i]) { return false; }
        }
        return true;
    }

    static bool IsForbiddenMethod(string full, string memberName)
    {
        string signature = full + "." + memberName;
        for (int i = 0; i < ForbiddenMethods.Length; i++)
        {
            if (signature == ForbiddenMethods[i]) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Opcodes whose token names a method or field. Deliberately only these:
    /// resolving a *type* token (castclass, newarr, ...) as a member is what makes
    /// Mono abort in native code, uncatchably.
    ///
    /// ldftn / ldvirtftn are in the list because that is how a delegate is built,
    /// and an anonymous method hides its target behind one.
    /// </summary>
    static bool TakesMemberToken(int op)
    {
        switch (op)
        {
            case 0x28:                                  // call
            case 0x6F:                                  // callvirt
            case 0x73:                                  // newobj
            case 0x7B: case 0x7C:                       // ldfld, ldflda
            case 0x7D:                                  // stfld
            case 0x7E: case 0x7F:                       // ldsfld, ldsflda
            case 0x80:                                  // stsfld
            case 0xFE06: case 0xFE07:                   // ldftn, ldvirtftn
                return true;
        }
        return false;
    }

    /// <summary>
    /// Operand size in bytes, or -1 for `switch`.
    ///
    /// This is spelled out per ECMA-335 rather than guessed at by range, because a
    /// single wrong size desynchronises the whole walk and the next "token" read is
    /// garbage -- which surfaces as a native assertion inside Mono that no catch
    /// block can help with. `conv.u8` (0x6E) sitting one past a range boundary was
    /// exactly that bug.
    /// </summary>
    static int OperandSize(int op)
    {
        if (op >= 0xFE00)
        {
            switch (op)
            {
                case 0xFE06: case 0xFE07:               // ldftn, ldvirtftn
                case 0xFE15: case 0xFE16: case 0xFE1C:  // initobj, constrained., sizeof
                    return 4;
                case 0xFE09: case 0xFE0A: case 0xFE0B:  // ldarg, ldarga, starg
                case 0xFE0C: case 0xFE0D: case 0xFE0E:  // ldloc, ldloca, stloc
                    return 2;
                case 0xFE12: case 0xFE19:               // unaligned., no.
                    return 1;
            }
            return 0;
        }

        switch (op)
        {
            case 0x45: return -1;                                    // switch

            // int8 operand
            case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13:
            case 0x1F: case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F:
            case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
            case 0x36: case 0x37: case 0xDE:
                return 1;

            // int64 / float64 operand
            case 0x21: case 0x23:
                return 8;

            // int32, token or branch target
            case 0x20: case 0x22: case 0x27: case 0x28: case 0x29:
            case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D:
            case 0x3E: case 0x3F: case 0x40: case 0x41: case 0x42: case 0x43:
            case 0x44: case 0x6F: case 0x70: case 0x71: case 0x72: case 0x73:
            case 0x74: case 0x75: case 0x79: case 0x7B: case 0x7C: case 0x7D:
            case 0x7E: case 0x7F: case 0x80: case 0x81: case 0x8D: case 0x8F:
            case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0:
            case 0xDD:
                return 4;
        }
        return 0;   // everything else is a bare instruction
    }
}
