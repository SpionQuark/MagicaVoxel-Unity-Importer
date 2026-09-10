using UnityEngine;

namespace Lunatic.Voxel
{
    /// <summary>
    /// Plays a MagicaVoxel frame-by-frame ("mesh swap") animation by toggling a set
    /// of child "frame_*" GameObjects on and off.
    ///
    /// The .vox importer prefers a real AnimationClip + AnimatorController for this,
    /// and only falls back to this component when an AnimatorController could not be
    /// embedded. It also runs in player builds with no editor dependency.
    /// </summary>
    [AddComponentMenu("Lunatic/Vox Frame Animator")]
    public class VoxFrameAnimator : MonoBehaviour
    {
        [Tooltip("Frame GameObjects, shown one at a time in order.")]
        public GameObject[] frames = new GameObject[0];

        [Min(0.01f)]
        [Tooltip("Playback speed in frames per second.")]
        public float framesPerSecond = 12f;

        public bool loop = true;
        public bool playOnEnable = true;

        private float _time;
        private int _shown = -1;

        private void OnEnable()
        {
            if (!playOnEnable) return;
            _time = 0f;
            Show(0);
        }

        public void Rewind()
        {
            _time = 0f;
            Show(0);
        }

        private void Update()
        {
            if (frames == null || frames.Length == 0 || framesPerSecond <= 0f) return;

            _time += Time.deltaTime;
            int frame = Mathf.FloorToInt(_time * framesPerSecond);

            if (frame >= frames.Length)
            {
                if (!loop)
                {
                    Show(frames.Length - 1);
                    enabled = false;
                    return;
                }
                frame %= frames.Length;
            }

            Show(frame);
        }

        private void Show(int index)
        {
            if (index == _shown || frames == null || frames.Length == 0) return;
            _shown = index;
            for (int i = 0; i < frames.Length; i++)
                if (frames[i] != null)
                    frames[i].SetActive(i == index);
        }
    }
}
