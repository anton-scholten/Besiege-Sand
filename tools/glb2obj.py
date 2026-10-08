#!/usr/bin/env python3
"""Converts a glTF-binary (.glb) into the Wavefront .obj a Besiege block wants.

    python3 tools/glb2obj.py in.glb out.obj [--fit=1.0] [--raw]

Besiege's <Mesh> resource takes .obj, and poly.pizza serves .glb, so something has
to bridge the two and Blender is a heavy dependency for one crate. Only what a
single static mesh needs is handled: POSITION, NORMAL, TEXCOORD_0 and triangle
indices, no animation, no skinning, no draco.

By default the output is put into Besiege's block convention, which is not glTF's:

  * a block mesh is **Z-up and centred on origin** (notes repo, 02-blocks.md);
  * glTF is right-handed and Unity is left-handed, so the map has to *flip*
    handedness: (x, y, z) -> (-x, -z, y). The obvious handedness-preserving swap
    (x, -z, y) gives a mesh that measures correctly and is mirrored -- which is
    invisible on a crate and very visible on anything with writing or a keyboard;
  * that map has determinant -1, so every triangle's winding is reversed here too.
    Leave it alone and the faces are inside out, which reads as a mesh that is
    simply not there.

--fit=S scales the result so its largest extent is S (default 1.0), because
exported models arrive at whatever scale their author worked in -- this one came
in at roughly 400000 units across.
--raw skips all of the above and converts coordinates unchanged.
--zup is for a mesh authored Z-up whose node turns it Y-up (what an FBX export to glTF gives): the
node's turn is not applied, so the mesh's own Z is the block's Z, and only the handedness is
flipped, (x, y, z) -> (-x, y, z). Used for the sprayer's pipe, whose open end is its own +Z.
--uv makes texture coordinates where the mesh has none, wrapped round its Z axis (u round, v
up), so a texture like the sprayer's cardboard lies on it.
"""
import math
import json
import struct
import sys

COMPONENT = {5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2),
             5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
COUNT = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}


def read(path):
    d = open(path, 'rb').read()
    _, _, length = struct.unpack_from('<4sII', d, 0)
    off, chunks = 12, {}
    while off < length:
        clen, ctype = struct.unpack_from('<I4s', d, off)
        off += 8
        chunks[ctype.strip(b'\0')] = d[off:off + clen]
        off += clen
    return json.loads(chunks[b'JSON'].decode('utf8')), chunks[b'BIN']


def accessor(gltf, blob, index):
    acc = gltf['accessors'][index]
    view = gltf['bufferViews'][acc['bufferView']]
    fmt, size = COMPONENT[acc['componentType']]
    n = COUNT[acc['type']]
    base = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
    stride = view.get('byteStride') or size * n
    out = []
    for i in range(acc['count']):
        out.append(struct.unpack_from('<' + fmt * n, blob, base + i * stride))
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    flags = [a for a in sys.argv[1:] if a.startswith('--')]
    if len(args) < 2:
        sys.exit(__doc__)
    fit = 1.0
    for f in flags:
        if f.startswith('--fit'):
            fit = float(f.split('=', 1)[1]) if '=' in f else 1.0
    raw = '--raw' in flags
    zup = '--zup' in flags
    makeuv = '--uv' in flags

    def to_block(p):
        if raw:
            return p
        if zup:
            return (-p[0], p[1], p[2])
        return (-p[0], -p[2], p[1])

    gltf, blob = read(args[0])

    # Two passes: the first only to learn the bounds, because centring and fitting
    # cannot be decided per vertex.
    lo = [1e30, 1e30, 1e30]
    hi = [-1e30, -1e30, -1e30]
    for mesh in gltf.get('meshes', []):
        for prim in mesh['primitives']:
            if prim.get('mode', 4) != 4:
                continue
            for p in accessor(gltf, blob, prim['attributes']['POSITION']):
                q = to_block(p)
                for k in range(3):
                    lo[k] = min(lo[k], q[k])
                    hi[k] = max(hi[k], q[k])
    centre = [(lo[k] + hi[k]) * 0.5 for k in range(3)]
    extent = max(hi[k] - lo[k] for k in range(3))
    scale = 1.0 if raw or extent <= 0 else fit / extent
    lines = ["# converted from %s by tools/glb2obj.py" % args[0]]
    vbase = nbase = tbase = 1

    for mesh in gltf.get('meshes', []):
        for prim in mesh['primitives']:
            if prim.get('mode', 4) != 4:
                continue
            attrs = prim['attributes']
            pos = accessor(gltf, blob, attrs['POSITION'])
            nor = accessor(gltf, blob, attrs['NORMAL']) if 'NORMAL' in attrs else []
            uv = accessor(gltf, blob, attrs['TEXCOORD_0']) if 'TEXCOORD_0' in attrs else []
            if not uv and makeuv:
                # Round the block's Z: u the angle, v the height, in glTF's V-down sense (flipped below).
                bz = [to_block(p)[2] for p in pos]
                z0, z1 = min(bz), max(bz)
                uv = []
                for p in pos:
                    q = to_block(p)
                    u = (math.atan2(q[1], q[0]) / (2 * math.pi)) % 1.0
                    v = 1.0 - (q[2] - z0) / max(z1 - z0, 1e-9)
                    uv.append((u, v))
            idx = [i[0] for i in accessor(gltf, blob, prim['indices'])]

            for p in pos:
                q = to_block(p)
                lines.append("v %.6f %.6f %.6f" % (
                    (q[0] - centre[0]) * scale,
                    (q[1] - centre[1]) * scale,
                    (q[2] - centre[2]) * scale))
            for p in nor:
                q = to_block(p)
                lines.append("vn %.6f %.6f %.6f" % q)
            # glTF's V runs down, .obj's runs up.
            for p in uv:
                lines.append("vt %.6f %.6f" % (p[0], 1.0 - p[1]))

            lines.append("g %s" % mesh.get('name', 'mesh'))
            for i in range(0, len(idx) - 2, 3):
                # The coordinate map reflects, so winding is reversed to keep the
                # faces pointing out. --raw leaves the order alone.
                if raw:
                    a, b, c = idx[i] + vbase, idx[i + 1] + vbase, idx[i + 2] + vbase
                else:
                    a, b, c = idx[i + 2] + vbase, idx[i + 1] + vbase, idx[i] + vbase
                if uv and nor:
                    lines.append("f %d/%d/%d %d/%d/%d %d/%d/%d" % (a, a, a, b, b, b, c, c, c))
                elif nor:
                    lines.append("f %d//%d %d//%d %d//%d" % (a, a, b, b, c, c))
                else:
                    lines.append("f %d %d %d" % (a, b, c))
            vbase += len(pos)
            nbase += len(nor)
            tbase += len(uv)

    open(args[1], 'w').write("\n".join(lines) + "\n")
    print("wrote %s: %d lines" % (args[1], len(lines)))


if __name__ == '__main__':
    main()
