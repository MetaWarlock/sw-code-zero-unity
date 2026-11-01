using UnityEngine;

namespace CodeZero.DDuck
{
    /// <summary>Small reusable shooting target for the sandbox, not an original-game enemy.</summary>
    public sealed class DDuckTarget : MonoBehaviour
    {
        public int Hits { get; private set; }
        SpriteRenderer visual;
        float flash;
        void Awake() { visual = GetComponent<SpriteRenderer>(); }
        public void Hit() { Hits++; flash = .15f; }
        void Update()
        {
            flash = Mathf.Max(0, flash - Time.deltaTime);
            if (visual) visual.color = flash > 0 ? Color.white : new Color(.85f, .37f, .22f);
        }
    }
}
