using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sand's readout, written to the log on F9 and at no other time: what the field sees --
    /// grains, the bed, the governor, the last dig, the last block pressing on the sand. It used
    /// to report the device, the scene's colliders and a CPU benchmark at every load as well;
    /// what those were for is in docs/FINDINGS.md.
    /// </summary>
    public class ProbeRunner : MonoBehaviour
    {
        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F9)) return;
            SandField field = SandField.Existing;
            if (field == null) { Debug.Log("[Sand] sand field: not created yet"); return; }
            System.Collections.Generic.List<string> lines = field.Describe();
            for (int i = 0; i < lines.Count; i++) Debug.Log("[Sand] " + lines[i]);
        }
    }
}
