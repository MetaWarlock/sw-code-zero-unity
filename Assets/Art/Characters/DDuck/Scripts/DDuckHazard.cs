using UnityEngine;

namespace CodeZero.DDuck
{
    public sealed class DDuckHazard : MonoBehaviour
    {
        [SerializeField] int hurtAnimation = 9;
        void OnTriggerStay2D(Collider2D other)
        {
            var player = other.GetComponentInParent<DDuckController>();
            if (player) player.ReceiveHit(transform.position, hurtAnimation);
        }
    }
}
