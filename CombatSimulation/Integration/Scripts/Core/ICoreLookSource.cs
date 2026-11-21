using System.Collections.Generic;
using CombatSimulation.Core;
using GameDatabase.DataModel;
using GameDatabase.Model;

namespace Combat.Integration.Core
{
    // What CoreBattleHost reads from a game's ship mapper (phase 9, step 2, S4): the mapping of each mapped ship and the
    // looks that the mapper collected. Each game's CoreShipMapper implements it. The dictionaries only grow.
    public interface ICoreLookSource
    {
        IReadOnlyDictionary<ShipDefinition, CoreShipMapping> Mappings { get; }
        IReadOnlyDictionary<object, CoreBulletSource> BulletSources { get; }
        IReadOnlyDictionary<BulletTriggerDefinition, BulletTrigger_SpawnBullet> SpawnTriggers { get; }
        IReadOnlyDictionary<WormTailDefinition, CoreSegmentSource> SegmentSources { get; }
        IReadOnlyDictionary<RepairBotDefinition, ColorData> RepairBotColors { get; }
        IReadOnlyDictionary<RepairBotDefinition, AudioClipId> RepairBotSounds { get; }
        IReadOnlyDictionary<RepairBotDefinition, VisualEffect> RepairBotEffects { get; }
        IReadOnlyDictionary<ShipDefinition, ColorData> DecoyColors { get; }
        IReadOnlyDictionary<ShipDefinition, AudioClipId> DecoySounds { get; }
        IReadOnlyDictionary<object, int> WeaponDevices { get; }
        IReadOnlyDictionary<ShieldDefinition, CoreShieldLook> ShieldLooks { get; }
    }
}
