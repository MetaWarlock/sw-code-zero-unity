using UnityEngine;
using UnityEngine.InputSystem;

namespace CodeZero.DDuck
{
    /// <summary>
    /// Native Physics2D sandbox actor. ROM-derived movement at 16 pixels/unit, 60 Hz;
    /// does not execute PlayerCore or emulate the NES tile/camera scheduler.
    /// See Art/Research/DarkwingDuck/docs/player_mechanics.xhtml §§3, 6–8.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D), typeof(PlayerInput))]
    public sealed class DDuckController : MonoBehaviour
    {
        public const float PixelsPerUnit = 16f;
        public const float ReferenceHz = 60f;
        public const float WalkSpeed = 1.5f * ReferenceHz / PixelsPerUnit;
        public const float JumpSpeed = (4f + 229f / 256f) * ReferenceHz / PixelsPerUnit;
        public const float Gravity = .25f * ReferenceHz * ReferenceHz / PixelsPerUnit;
        public const float FallSpeed = 8f * ReferenceHz / PixelsPerUnit;
        public const float JumpCutSpeed = -(1f - 171f / 256f) * ReferenceHz / PixelsPerUnit;
        // A0B/A0C glove at strip y=7, relative to the shared (24,24) sprite origin.
        public static readonly Vector2 HookGripOffset = new Vector2(0, 17f / PixelsPerUnit);

        [SerializeField] Animator animator;
        [SerializeField] SpriteRenderer spriteRenderer;
        [SerializeField] DDuckProjectile projectilePrefab;
        [SerializeField] LayerMask environmentMask = 1; // Default; actor uses Ignore Raycast.
        [SerializeField] bool playEntrance = true;
        [SerializeField] Vector2 standingSize = new Vector2(1.375f, 1.5f);
        [SerializeField] Vector2 crouchingSize = new Vector2(1.375f, 1f);

        Rigidbody2D body;
        BoxCollider2D bodyCollider;
        PlayerInput playerInput;
        FixedJoint2D hookJoint;
        InputAction moveAction, jumpAction, fireAction, restartAction;
        readonly RaycastHit2D[] groundHits = new RaycastHit2D[8];
        readonly Collider2D[] ceilingHits = new Collider2D[8];
        ContactFilter2D environmentFilter;
        Vector2 move;
        Vector2 spawn;
        bool jumpQueued, fireQueued, jumpHeld, wasGrounded, crouched, facingRight = true;
        float shotRemaining, hurtRemaining, invulnerableRemaining, entranceRemaining, hookCooldown;
        float deathElapsed, knockbackDirection;
        int pose = -1, hurtPose = 9, activeShots;
        DDuckHook hook;
        bool defeated, celebrating;

        public bool Grounded { get; private set; }
        public bool Hanging => hook != null;
        public bool Crouched => crouched;
        public bool FacingRight => facingRight;
        public bool IsDeflecting => !defeated && Grounded && !crouched && move.y > .5f && Mathf.Abs(move.x) < .5f && shotRemaining <= 0 && hurtRemaining <= 0;
        public int Health { get; private set; } = 4;
        public int AnimationId => pose;
        public int ActiveShots => activeShots;
        public Rigidbody2D Body => body;

        void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            bodyCollider = GetComponent<BoxCollider2D>();
            playerInput = GetComponent<PlayerInput>();
            hookJoint = GetComponent<FixedJoint2D>();
            if (!hookJoint) hookJoint = gameObject.AddComponent<FixedJoint2D>();
            hookJoint.enabled = false;
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (!spriteRenderer) spriteRenderer = GetComponentInChildren<SpriteRenderer>();
            environmentFilter = new ContactFilter2D { useLayerMask = true, layerMask = environmentMask, useTriggers = false };
            spawn = body.position;
            body.bodyType = RigidbodyType2D.Dynamic;
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            body.gravityScale = Gravity / Mathf.Max(.01f, Mathf.Abs(Physics2D.gravity.y));
            entranceRemaining = playEntrance ? .4f : 0;
        }

        void Start()
        {
            moveAction = playerInput.actions.FindAction("Gameplay/Move", true);
            jumpAction = playerInput.actions.FindAction("Gameplay/Jump", true);
            fireAction = playerInput.actions.FindAction("Gameplay/Fire", true);
            restartAction = playerInput.actions.FindAction("Gameplay/Restart", true);
        }

        void Update()
        {
            if (moveAction == null) return;
            move = moveAction.ReadValue<Vector2>();
            jumpHeld = jumpAction.IsPressed();
            jumpQueued |= jumpAction.WasPressedThisFrame();
            fireQueued |= fireAction.WasPressedThisFrame();
            if (restartAction.WasPressedThisFrame()) Respawn();
            if (spriteRenderer)
            {
                spriteRenderer.flipX = !facingRight;
                spriteRenderer.enabled = invulnerableRemaining <= 0 || ((int)(Time.time * 60) & 4) != 0;
            }
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            shotRemaining = Mathf.Max(0, shotRemaining - dt);
            hurtRemaining = Mathf.Max(0, hurtRemaining - dt);
            invulnerableRemaining = Mathf.Max(0, invulnerableRemaining - dt);
            hookCooldown = Mathf.Max(0, hookCooldown - dt);
            Grounded = ProbeGround();

            if (defeated)
            {
                deathElapsed += dt;
                SetPose(9);
                if (deathElapsed > 2 || body.position.y < -15) Respawn();
                ConsumeButtons();
                return;
            }
            if (body.position.y < -15) { Defeat(); ConsumeButtons(); return; }
            if (celebrating) { body.linearVelocityX = 0; ConsumeButtons(); return; }
            if (entranceRemaining > 0)
            {
                entranceRemaining -= dt;
                body.linearVelocityX = 0;
                SetPose(0x2C);
                ConsumeButtons();
                return;
            }
            if (Hanging)
            {
                if (Mathf.Abs(move.x) > .5f) facingRight = move.x > 0;
                if (jumpQueued || move.y < -.5f)
                {
                    ReleaseHook();
                    if (jumpQueued) body.linearVelocityY = JumpSpeed;
                    else body.linearVelocityY = JumpCutSpeed;
                }
                else
                {
                    if (fireQueued) Fire();
                    SetPose(shotRemaining > 0 ? 0x0C : 0x0B);
                    ConsumeButtons();
                    return;
                }
            }
            if (hurtRemaining > 0)
            {
                body.linearVelocityX = knockbackDirection * (.5f * ReferenceHz / PixelsPerUnit);
                SetPose(hurtPose);
                ConsumeButtons();
                return;
            }

            bool wantsCrouch = Grounded && move.y < -.5f;
            if (!wantsCrouch && crouched && !CanStand()) wantsCrouch = true;
            SetCrouched(wantsCrouch);
            if (Mathf.Abs(move.x) > .5f && !crouched && shotRemaining <= 0) facingRight = move.x > 0;
            bool jumped = jumpQueued && Grounded && !crouched;
            if (jumped)
            {
                body.linearVelocityY = JumpSpeed;
                Grounded = false;
            }
            if (!jumped && !jumpHeld && body.linearVelocityY > 0) body.linearVelocityY = JumpCutSpeed;
            if (fireQueued && !IsDeflecting) Fire();
            bool heldPose = crouched || IsDeflecting || (Grounded && shotRemaining > 0);
            body.linearVelocityX = heldPose ? 0 : Mathf.Sign(move.x) * (Mathf.Abs(move.x) > .5f ? WalkSpeed : 0);
            body.linearVelocityY = Mathf.Max(-FallSpeed, body.linearVelocityY);

            if (Grounded)
                SetPose(shotRemaining > 0 ? (crouched ? 7 : 5) : crouched ? 6 : IsDeflecting ? 4 : Mathf.Abs(body.linearVelocityX) > .01f ? 3 : 1);
            else
                SetPose(shotRemaining > 0 ? (body.linearVelocityY > 0 ? 0x0E : 0x0F) : move.y > .5f ? 0x0D : 0x11);
            if (Grounded && !wasGrounded && body.linearVelocityY <= 0 && shotRemaining <= 0) SetPose(crouched ? 6 : 1);
            wasGrounded = Grounded;
            ConsumeButtons();
        }

        void ConsumeButtons() { jumpQueued = fireQueued = false; }

        bool ProbeGround()
        {
            if (body.linearVelocityY > .1f || Hanging) return false;
            int count = bodyCollider.Cast(Vector2.down, environmentFilter, groundHits, .045f);
            for (int i = 0; i < count; ++i)
                if (groundHits[i].normal.y > .65f) return true;
            return false;
        }

        bool CanStand()
        {
            var center = body.position + new Vector2(0, standingSize.y * .5f - .1f);
            return Physics2D.OverlapBox(center, new Vector2(standingSize.x * .9f, .15f), 0, environmentFilter, ceilingHits) == 0;
        }

        void SetCrouched(bool value)
        {
            if (value == crouched) return;
            crouched = value;
            bodyCollider.size = value ? crouchingSize : standingSize;
            bodyCollider.offset = value ? new Vector2(0, (crouchingSize.y - standingSize.y) * .5f) : Vector2.zero;
        }

        void Fire()
        {
            // Original normal weapon: edge-triggered, at most three live shots.
            if (!projectilePrefab || activeShots >= 3 || shotRemaining > 0) return;
            shotRemaining = 12f / ReferenceHz;
            var offset = new Vector2((facingRight ? 15 : -15) / PixelsPerUnit, Grounded ? (crouched ? -8 : 3) / PixelsPerUnit : 0);
            var projectile = Instantiate(projectilePrefab, body.position + offset, Quaternion.identity);
            activeShots++;
            projectile.Launch(this, facingRight ? 1 : -1);
        }

        public void NotifyProjectileEnded() { activeShots = Mathf.Max(0, activeShots - 1); }

        public bool TryGrabHook(DDuckHook candidate)
        {
            if (defeated || celebrating || Hanging || Grounded || hookCooldown > 0 || hurtRemaining > 0) return false;
            Vector2 hand = body.position + HookGripOffset;
            if (Vector2.Distance(hand, candidate.Anchor) > .5f) return false;
            hook = candidate;
            body.position = candidate.Anchor - HookGripOffset;
            body.linearVelocity = Vector2.zero;
            hookJoint.autoConfigureConnectedAnchor = false;
            hookJoint.anchor = HookGripOffset;
            hookJoint.connectedAnchor = candidate.Anchor;
            hookJoint.enabled = true;
            SetCrouched(false);
            SetPose(0x0B);
            return true;
        }

        void ReleaseHook()
        {
            hook = null;
            hookJoint.enabled = false;
            hookCooldown = .25f;
        }

        public void ReceiveHit(Vector2 source, int animationId = 9)
        {
            if (defeated || invulnerableRemaining > 0) return;
            ReleaseHook();
            celebrating = false;
            entranceRemaining = 0;
            Health--;
            if (Health <= 0) { Defeat(); return; }
            hurtPose = animationId == 0x1C || animationId == 0x1D ? animationId : 9;
            hurtRemaining = 15f / ReferenceHz;
            invulnerableRemaining = 60f / ReferenceHz;
            knockbackDirection = Mathf.Sign(body.position.x - source.x);
            if (knockbackDirection == 0) knockbackDirection = facingRight ? -1 : 1;
            body.linearVelocityY = JumpCutSpeed;
            SetPose(hurtPose);
        }

        public void Defeat()
        {
            if (defeated) return;
            ReleaseHook();
            defeated = true;
            deathElapsed = 0;
            Health = 0;
            bodyCollider.enabled = false;
            body.linearVelocity = new Vector2(0, 2f * ReferenceHz / PixelsPerUnit);
            SetPose(9);
        }

        public void Celebrate(bool carried = false)
        {
            if (defeated) return;
            ReleaseHook();
            celebrating = true;
            body.linearVelocity = Vector2.zero;
            SetPose(carried ? 0x2D : 0x2B);
        }

        public void Respawn()
        {
            ReleaseHook();
            defeated = celebrating = false;
            Health = 4;
            hurtRemaining = invulnerableRemaining = shotRemaining = 0;
            bodyCollider.enabled = true;
            SetCrouched(false);
            body.position = spawn;
            body.linearVelocity = Vector2.zero;
            entranceRemaining = playEntrance ? .4f : 0;
            spriteRenderer.enabled = true;
            ConsumeButtons();
        }

        void SetPose(int id)
        {
            if (id == pose || !animator) return;
            pose = id;
            animator.SetInteger("AnimationId", id);
            animator.SetTrigger("ChangePose");
            animator.SetBool("Grounded", Grounded);
            animator.SetFloat("Speed", Mathf.Abs(body.linearVelocityX));
            animator.SetFloat("VerticalSpeed", body.linearVelocityY);
        }
    }
}
