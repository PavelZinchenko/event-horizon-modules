using UnityEngine;

namespace Combat.Integration.Core
{
    // The camera rule of a game (phase 9, step 2, S4): Main frames the player and the target with legacy ViewRect,
    // Frontier with legacy ArcadeModeView, which also reads the player's heading, velocity and size.
    public interface ICameraFraming
    {
        // The area the camera must show, and its centre, in Unity units.
        Rect Rect { get; }
        Vector2 Center { get; }

        // Zooms in on a new player ship.
        void Zoom();

        // Once per frame, after the views have their drawn poses. Null for a ship that is not active: the framing keeps
        // its last position.
        void Update(CoreShipView player, CoreShipView target, bool playerInTheMiddle);
    }
}
