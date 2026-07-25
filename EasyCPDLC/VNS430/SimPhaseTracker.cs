namespace EasyCPDLC.VNS430
{
    /// <summary>
    /// Minimal flight-phase memory fed by sim telemetry (SimConnect), for the parts of
    /// the app that only need "has this flight reached cruise yet".
    /// </summary>
    /// <remarks>
    /// The VATSIM-data phase engine cannot run without a VATSIM connection, which is
    /// the normal state on SI. This tracker needs only altitude and on-ground: cruise
    /// is remembered from the moment the enroute altitude is crossed, and a landing
    /// (ground after having been airborne) resets it for the next leg. Restarting the
    /// app mid-cruise recovers on the first telemetry packet.
    /// </remarks>
    internal sealed class SimPhaseTracker
    {
        // Matches the VATSIM phase engine's enroute marker.
        internal const double CruiseAltitudeFt = 22000;

        internal bool AirborneSeen { get; private set; }
        internal bool ReachedCruise { get; private set; }

        internal void Update(double altitudeFt, bool onGround)
        {
            if (onGround)
            {
                // Ground after flight = landed: the next leg starts fresh. Ground
                // before flight (taxi-out) changes nothing.
                if (AirborneSeen)
                {
                    AirborneSeen = false;
                    ReachedCruise = false;
                }
                return;
            }

            AirborneSeen = true;
            if (altitudeFt >= CruiseAltitudeFt)
            {
                ReachedCruise = true;
            }
        }
    }
}
