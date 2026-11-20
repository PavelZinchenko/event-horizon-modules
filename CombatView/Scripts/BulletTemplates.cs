using System.Collections.Generic;
using CombatView.Views;
using UnityEngine;

namespace CombatView
{
    // One template per bullet look (migration phase 5b, step 5), as legacy PrefabCache.GetBulletPrefab: a configured
    // copy of the shape's prefab, kept inactive in the pool storage. The instances come from the template, so the
    // prefab itself never changes.
    internal sealed class BulletTemplates
    {
        public BulletTemplates(PrefabPool prefabs)
        {
            _prefabs = prefabs;
        }

        // Null for a missing prefab.
        public GameObject Get(BulletLook look)
        {
            if (_templates.TryGetValue(look, out var template))
                return template;

            var prefab = _prefabs.Load(PrefabPath(look.Kind));
            if (prefab)
            {
                template = _prefabs.CreateTemplate(prefab);
                var setup = template.GetComponent<BulletViewSetup>();
                if (setup)
                    setup.Configure(look);
                else
                    template = null;
            }

            _templates.Add(look, template);
            return template;
        }

        public void Clear() => _templates.Clear();

        // Legacy PrefabCache.GetBulletPrefab.
        private static string PrefabPath(BulletViewKind kind)
        {
            switch (kind)
            {
                case BulletViewKind.Rocket: return CombatViewAssets.RocketBulletPrefab;
                case BulletViewKind.Laser: return CombatViewAssets.LaserBulletPrefab;
                case BulletViewKind.Lightning: return CombatViewAssets.LightningBulletPrefab;
                case BulletViewKind.EnergyBeam: return CombatViewAssets.EnergyBeamBulletPrefab;
                case BulletViewKind.PiercingLaser: return CombatViewAssets.PiercingLaserBulletPrefab;
                case BulletViewKind.Spark: return CombatViewAssets.SparkBulletPrefab;
                case BulletViewKind.Mine: return CombatViewAssets.MineBulletPrefab;
                case BulletViewKind.Arc: return CombatViewAssets.ArcBulletPrefab;
                case BulletViewKind.BlackHole: return CombatViewAssets.BlackHoleBulletPrefab;
                case BulletViewKind.Harpoon: return CombatViewAssets.HarpoonBulletPrefab;
                case BulletViewKind.CircularSaw: return CombatViewAssets.CircularSawBulletPrefab;
                default: return CombatViewAssets.ProjectileBulletPrefab;
            }
        }

        private readonly PrefabPool _prefabs;
        private readonly Dictionary<BulletLook, GameObject> _templates = new Dictionary<BulletLook, GameObject>();
    }
}
