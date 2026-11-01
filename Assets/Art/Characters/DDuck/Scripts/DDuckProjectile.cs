using UnityEngine;

namespace CodeZero.DDuck
{
    [RequireComponent(typeof(Rigidbody2D), typeof(CircleCollider2D))]
    public sealed class DDuckProjectile : MonoBehaviour
    {
        [SerializeField] Animator animator;
        DDuckController owner;
        Rigidbody2D body;
        Collider2D hitbox;
        float age;
        bool burst, counted;
        int direction;
        public DDuckController Owner => owner;

        public void Launch(DDuckController shooter, int facing)
        {
            owner = shooter;
            counted = true;
            direction = facing;
            body = GetComponent<Rigidbody2D>();
            hitbox = GetComponent<Collider2D>();
            body.bodyType = RigidbodyType2D.Kinematic;
            body.gravityScale = 0;
            body.linearVelocity = new Vector2(facing * 4f * 60f / 16f, 0);
            if (!animator) animator = GetComponentInChildren<Animator>();
            GetComponentInChildren<SpriteRenderer>().flipX = facing < 0;
            foreach (var collider in shooter.GetComponentsInChildren<Collider2D>()) Physics2D.IgnoreCollision(hitbox, collider);
        }

        void FixedUpdate()
        {
            age += Time.fixedDeltaTime;
            if (!burst && age >= 12f / 60f)
            {
                body.linearVelocityX = direction * 6f * 60f / 16f;
                animator.SetBool("Cloud", true);
            }
            if (age > 2f) Explode();
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (burst || other.isTrigger || other.GetComponentInParent<DDuckProjectile>()) return;
            var player = other.GetComponentInParent<DDuckController>();
            if (player == owner) return;
            if (player)
            {
                if (player.IsDeflecting)
                {
                    if (counted && owner) owner.NotifyProjectileEnded();
                    owner = player;
                    counted = false;
                    direction = player.FacingRight ? 1 : -1;
                    body.linearVelocityX = direction * 6f * 60f / 16f;
                    return;
                }
                player.ReceiveHit(transform.position);
            }
            var target = other.GetComponentInParent<DDuckTarget>();
            if (target) target.Hit();
            Explode();
        }

        void Explode()
        {
            if (burst) return;
            burst = true;
            body.linearVelocity = Vector2.zero;
            hitbox.enabled = false;
            animator.SetTrigger("Burst");
            Destroy(gameObject, 12f / 60f);
        }

        void OnDestroy() { if (counted && owner) owner.NotifyProjectileEnded(); }
    }
}
