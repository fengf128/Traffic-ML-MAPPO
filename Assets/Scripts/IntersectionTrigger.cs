using UnityEngine;

public class IntersectionTrigger : MonoBehaviour
{
    [Header("Parent Agent (this intersection's grid position)")]
    public TrafficSignalAgent parentAgent;

    [Header("Left-turn Route Config")]
    public RouteConfig leftRoute;

    [Header("Straight / Right-turn Route Config")]
    public RouteConfig straightRightRoute;

    [Header("Dedicated Lane Landing Points (snapped instantly on trigger)")]
    public Transform leftLaneLanding;
    public Transform straightRightLaneLanding;

    private void OnTriggerEnter(Collider other)
    {
        CarController car = other.GetComponentInParent<CarController>();
        if (car == null) return;

        int targetX = car.targetGridX;
        int targetZ = car.targetGridZ;
        int myX     = parentAgent.gridX;
        int myZ     = parentAgent.gridZ;

        Vector3 carForward = car.transform.forward;
        int diffX = targetX - myX;
        int diffZ = targetZ - myZ;

        bool isHorizontalExit = (targetX < 0 || targetX > 2);
        bool isVerticalExit  = (targetZ < 0 || targetZ > 2);

        Vector3 desiredDir = carForward;
        if (isHorizontalExit)
        {
            if (diffZ != 0)
                desiredDir = (diffZ > 0) ? Vector3.forward : Vector3.back;
            else
                desiredDir = (diffX > 0) ? Vector3.right : Vector3.left;
        }
        else if (isVerticalExit)
        {
            if (diffX != 0)
                desiredDir = (diffX > 0) ? Vector3.right : Vector3.left;
            else
                desiredDir = (diffZ > 0) ? Vector3.forward : Vector3.back;
        }
        else
        {
            if (diffX != 0) desiredDir = (diffX > 0) ? Vector3.right : Vector3.left;
            else if (diffZ != 0) desiredDir = (diffZ > 0) ? Vector3.forward : Vector3.back;
        }

        float angle = Vector3.SignedAngle(carForward, desiredDir, Vector3.up);

        RouteConfig selectedRoute = null;
        Transform   laneLanding   = null;
        Transform   turnTarget    = null;

        if (angle < -45f)
        {
            selectedRoute = leftRoute;
            laneLanding   = leftLaneLanding;
            turnTarget    = leftRoute?.leftTarget;
        }
        else if (angle > 45f)
        {
            selectedRoute = straightRightRoute;
            laneLanding   = straightRightLaneLanding;
            turnTarget    = straightRightRoute?.rightTarget;
        }
        else
        {
            selectedRoute = straightRightRoute;
            laneLanding   = straightRightLaneLanding;
            turnTarget    = straightRightRoute?.straightTarget;
        }

        if (selectedRoute != null)
        {
            car.TriggerLaneSwitchAndAssignRoute(laneLanding, selectedRoute, turnTarget);
        }
    }
}
