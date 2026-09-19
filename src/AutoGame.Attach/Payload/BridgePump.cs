#nullable disable
using UnityEngine;

namespace AutoGame
{
    public sealed class BridgePump : MonoBehaviour
    {
        void Update() { Bridge.PumpSafely(); }
    }
}
