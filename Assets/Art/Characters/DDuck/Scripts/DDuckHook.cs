using UnityEngine;

namespace CodeZero.DDuck
{
    [RequireComponent(typeof(CircleCollider2D))]
    public sealed class DDuckHook : MonoBehaviour
    {
        public Vector2 Anchor => transform.position;
        void OnTriggerStay2D(Collider2D other)
        {
            var player = other.GetComponentInParent<DDuckController>();
            if (player) player.TryGrabHook(this);
        }
    }
}
