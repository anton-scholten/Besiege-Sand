using System.Xml.Serialization;
using Modding.Modules;

namespace SandMod
{
    /// <summary>
    /// What Sprayer.xml deserialises into. This name, the element in the block XML
    /// and the AddBlockModule call in <see cref="Mod.OnLoad"/> all have to agree.
    ///
    /// The module carries nothing: every setting the player can reach is a mapper
    /// control on the behaviour, so that it saves with the machine and can be
    /// automated. Only MKey carries the automation feature, which is why the trigger
    /// is a key and the rest are plain settings.
    /// </summary>
    [XmlRoot("Sprayer")]
    public class SprayerModule : BlockModule
    {
    }
}
