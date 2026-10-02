using UnityEngine;

namespace SandMod
{
    // Every block here hangs a child object off itself -- a lens, a text mesh, a
    // particle system -- and has to cope with finding one already there, because
    // Besiege reuses a block's GameObject across reloads.
    public static class Attach
    {
        // The named descendant, or null. Inactive ones count: a lens or a shimmer
        // that was switched off would otherwise be missed, and a second one built
        // beside it on the next reload.
        public static Transform Find(Transform parent, string name)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        // The named child, created if it is not there yet. `created` is for the
        // callers whose placement is not idempotent and must run once only.
        public static GameObject Child(Transform parent, string name, out bool created)
        {
            Transform found = Find(parent, name);
            if (found != null)
            {
                created = false;
                return found.gameObject;
            }

            GameObject made = new GameObject();
            made.transform.name = name;
            made.transform.parent = parent;
            created = true;
            return made;
        }

        public static GameObject Child(Transform parent, string name)
        {
            bool created;
            return Child(parent, name, out created);
        }

        public static T Component<T>(GameObject target) where T : Component
        {
            T component = target.GetComponent<T>();
            return component == null ? target.AddComponent<T>() : component;
        }
    }
}
