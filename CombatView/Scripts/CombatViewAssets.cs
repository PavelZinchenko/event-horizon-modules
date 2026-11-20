using UnityEngine;

namespace CombatView
{
    // The assets that each game supplies to the shared views (Frontier's TODO/shared_views.md, V1). There is no
    // catalog: each game puts its own prefabs at Resources/CombatView/<path>, with the legacy names. Effect names from
    // the database load from EffectFolder. Every fixed prefab is a const string field here whose name ends in
    // "Prefab"; the Editor menu Tools/Combat/Check View Prefabs loads each one.
    public static class CombatViewAssets
    {
        public const string ResourceRoot = "CombatView/";
        public const string EffectFolder = "Effects/";

        // Below Resources, not ResourceRoot: the HSV material is shared with legacy (owner rule 3).
        public const string ShipHsvMaterial = "Materials/ShipHsv";

        // Null for a missing prefab.
        public static GameObject LoadPrefab(string path) => Resources.Load<GameObject>(ResourceRoot + path);

        // Ships, satellites and turrets (migration phase 5b, step 4).
        public const string ShipPrefab = "Ships/Default";
        public const string SatellitePrefab = "Satellites/default";
        public const string TurretPrefab = "Guns/turret";

        // Ship effects (step 4), explosions (step 8) and the exploration shadow (shared_views.md, V7).
        public const string EngineLightPrefab = "Effects/EngineLight";
        public const string ShipTrailPrefab = "Effects/ShipTrail";
        public const string DroneTrailPrefab = "Effects/DroneTrail";
        public const string MainShieldPrefab = "Effects/MainShield";
        public const string WavePrefab = "Effects/Wave";
        public const string WreckPrefab = "Effects/Wreck";
        public const string DamageTextPrefab = "Effects/DamageText";
        public const string FlashPrefab = "Effects/Flash";
        public const string FlashAdditivePrefab = "Effects/FlashAdditive";
        public const string SmokePrefab = "Effects/Smoke";
        public const string SmokeAdditivePrefab = "Effects/SmokeAdditive";
        public const string OrbAdditivePrefab = "Effects/OrbAdditive";
        public const string ShadowPrefab = "Effects/Shadow";

        // The element prefabs of the visual effect records (step 3; legacy PrefabCache.GetEffectPrefab).
        public const string SmokeSimplePrefab = "Effects/SmokeSimple";
        public const string SmokeSimpleAdditivePrefab = "Effects/SmokeSimpleAdditive";
        public const string SparkEffectPrefab = "Effects/Spark";
        public const string SpriteEffectPrefab = "Effects/Sprite";
        public const string LightningStrikePrefab = "Effects/LightningStrike";
        public const string LaserEffectPrefab = "Effects/Laser";

        // Device objects (step 7b), engine trails, asteroids and the planet (step 8).
        public const string TrailPrefab = "Objects/TrailRenderer";
        public const string DecoyPrefab = "Objects/Decoy";
        public const string EnergyShieldPrefab = "Objects/EnergyShield";
        public const string EnergyShieldOutlinePrefab = "Objects/EnergyShieldOutline";
        public const string FrontalShieldPrefab = "Objects/FrontalShield";
        public const string RepairBotPrefab = "Objects/RepairBot";
        public const string SegmentPrefab = "Objects/WormSegment";
        public const string Segment2Prefab = "Objects/WormSegment2";
        public const string AsteroidPrefab = "Objects/Asteroid";
        public const string PlanetPrefab = "Objects/Planet";

        // Bullet shapes (step 5; legacy PrefabCache.GetBulletPrefab).
        public const string ProjectileBulletPrefab = "Bullets/CommonProjectile";
        public const string RocketBulletPrefab = "Bullets/CommonRocket";
        public const string LaserBulletPrefab = "Bullets/Laser";
        public const string LightningBulletPrefab = "Bullets/Lightning";
        public const string EnergyBeamBulletPrefab = "Bullets/EnergyBeam";
        public const string PiercingLaserBulletPrefab = "Bullets/PiercingLaserBeam";
        public const string SparkBulletPrefab = "Bullets/Spark";
        public const string MineBulletPrefab = "Bullets/Mine";
        public const string ArcBulletPrefab = "Bullets/Arc";
        public const string BlackHoleBulletPrefab = "Bullets/WormHole";
        public const string HarpoonBulletPrefab = "Bullets/Hook";
        public const string CircularSawBulletPrefab = "Bullets/CircularSaw";
    }
}
