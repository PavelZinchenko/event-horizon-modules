using UnityEngine;

namespace CombatView.Effects
{
    // Copied from legacy Combat.Effects.ShadowEffect (Frontier's TODO/shared_views.md, V7; exploration plan E10): the
    // parent's sprite, bigger and dark, at a fixed world offset, so it stays below and to the right while the ship turns.
    // The material blurs it by _Size.
    [RequireComponent(typeof(SpriteRenderer))]
    public class ShadowEffect : EffectBase
    {
        [SerializeField] private float _effectSize;
        [SerializeField] private Vector2 _offset;

        protected override void OnInitialize()
        {
            Scale = _effectSize;
            GetComponent<SpriteRenderer>().sprite = transform.parent.GetComponent<SpriteRenderer>().sprite;
        }

        protected override void OnDispose() { }
        protected override void OnGameObjectDestroyed() { }

        protected override void OnAfterUpdate(float deltaTime)
        {
            var parent = transform.parent;
            transform.localPosition = Position + EffectMath.Rotate(_offset, -parent.localEulerAngles.z) / parent.localScale.z;
        }

        protected override void UpdateLife()
        {
        }

        protected override void UpdateSize()
        {
            base.UpdateSize();

            var size = transform.lossyScale.z;
            GetComponent<SpriteRenderer>().material.SetFloat("_Size", 0.1f / size);
        }
    }
}
