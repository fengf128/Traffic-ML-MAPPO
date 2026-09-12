using HealthbarGames;
using UnityEngine;

[System.Serializable]
public class RouteConfig
{
    public LaneType laneType;

    [Header("Common Points")]
    public Transform stopPoint;
    public TrafficLightBase trafficLight;
    public TrafficSignalAgent trafficAgent;

    [Header("Targets (per allowed turn)")]
    public Transform leftTarget;
    public Transform straightTarget;
    public Transform rightTarget;

    [Header("Final Destroy Points")]
    public Transform leftDestroyPoint;
    public Transform straightDestroyPoint;
    public Transform rightDestroyPoint;
}
