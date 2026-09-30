using System.Xml.Serialization;
using Modding.Modules;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// A deliberately empty block, for bisecting a fault the sprayer has and a stock
    /// block does not. Same mod, same loader, same XML shape, a module with no
    /// settings and a behaviour that adds one control and nothing else.
    ///
    /// If this block's mapper is also wrong, the fault is in the mod or the loader and
    /// nothing about the sprayer matters. If only the sprayer is wrong, it is the
    /// sprayer's own XML or behaviour. That is the only question worth answering
    /// first, and reasoning about modules and mapper keys answers none of it.
    /// </summary>
    [XmlRoot("SandControl")]
    public class ControlModule : BlockModule
    {
    }

    public class ControlBehaviour : BlockModuleBehaviour<ControlModule>
    {
        public override void SafeAwake()
        {
            AddSlider("Control", "SandControlValue", 1f, 0f, 10f);
            BlockReport.Dump("SandControl", gameObject);
        }
    }

    /// <summary>
    /// Lists what is actually on a block. The question "has another mod put its
    /// behaviour on my block?" has a direct answer and this is it.
    ///
    /// Type names come from <c>ToString</c>, not from reflection: a
    /// <c>GetType().Name</c> anywhere in the assembly is a
    /// <c>System.Reflection</c> reference, and the mod loader refuses the whole
    /// assembly over one of those.
    /// </summary>
    public class BlockReport
    {
        // Per label, not one flag for everything: the whole point is comparing the
        // sprayer's components against the control block's.
        private static readonly System.Collections.Generic.List<string> seen =
            new System.Collections.Generic.List<string>();

        public static void Dump(string label, GameObject block)
        {
            if (seen.Contains(label)) return;
            seen.Add(label);

            MonoBehaviour[] parts = block.GetComponentsInChildren<MonoBehaviour>(true);
            Debug.Log("[Sand] components on '" + label + "' (" + parts.Length + "):");
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                Debug.Log("[Sand]    " + parts[i].ToString());
            }
        }
    }
}
