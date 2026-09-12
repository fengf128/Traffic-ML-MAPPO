using UnityEngine;
using HealthbarGames;
using System.Collections;

public class CarController : MonoBehaviour
{
    [Header("Kinematics")]
    public float desiredSpeed = 5f;
    public float acceleration = 6f;
    public float deceleration = 10f;

    [Header("Stopping & Light")]
    public float stopDistance = 0.3f;
    public float lightCheckDistance = 3f;

    [Header("Following")]
    public float safeDistance = 3.0f;
    public LayerMask carLayer;

    [Header("Crab Walk (Inspector exposes duration)")]
    public float crabWalkDuration = 1.5f;
    public float crabWalkSpeed = 2f;

    private Transform stopPoint;
    private Transform exitPoint;
    private Transform destroyPoint;

    private TrafficLightBase trafficLight;
    private TrafficSignalAgent trafficAgent;
    private float currentSpeed = 0f;

    // 平滑转向速度（度/秒），控制车辆通过路口时的转向柔和程度
    private const float turnSpeed = 90f;

    public float CurrentSpeed => currentSpeed;

    public int targetGridX;
    public int targetGridZ;

    private enum State
    {
        ApproachingQueue,
        Queueing,
        PassingIntersection,
        CrabWalking,
        Leaving
    }

    private State state = State.ApproachingQueue;

    // Crab-walk runtime state
    private Transform crabWalkTarget;
    private float crabWalkStartX;
    private float crabWalkEndX;
    private float crabWalkElapsed;

    // Turn target: guide point used while passing through the intersection
    private Transform currentTurnTarget;

    // Ghost scoring guard: ensure each segment only scores once
    private bool hasPassedIntersection = false;
    private bool hasPassedExit = false;

    // ─────────────────────────────────────────────
    //  Initialization
    // ─────────────────────────────────────────────
    public void InitializeGlobal(int targetX, int targetZ, RouteConfig initRoute = null, Transform spawnTurnTarget = null)
    {
        this.targetGridX = targetX;
        this.targetGridZ = targetZ;

        if (initRoute != null)
        {
            stopPoint    = initRoute.stopPoint;
            trafficLight = initRoute.trafficLight;
            trafficAgent = initRoute.trafficAgent;
        }

        this.currentTurnTarget = spawnTurnTarget;
        state = State.ApproachingQueue;
    }

    public void AssignNextRoute(RouteConfig nextRoute)
    {
        if (nextRoute == null) return;

        stopPoint    = nextRoute.stopPoint;
        trafficLight = nextRoute.trafficLight;
        trafficAgent = nextRoute.trafficAgent;

        // exitPoint / destroyPoint are intentionally left from the previous route
        // so that if the car somehow exits the intersection without a new trigger
        // it still has a fallback target
        state = State.ApproachingQueue;
    }

    public void StartCrabWalk(Transform targetLane)
    {
        if (targetLane == null)
        {
            Debug.LogWarning($"[{name}] StartCrabWalk called with null target, skipped.");
            return;
        }

        crabWalkTarget   = targetLane;
        crabWalkStartX   = transform.localPosition.x;
        crabWalkEndX     = targetLane.position.x;
        crabWalkElapsed  = 0f;
        state = State.CrabWalking;
    }

    public void TriggerLaneSwitchAndAssignRoute(Transform landingPoint, RouteConfig nextRoute, Transform turnTarget)
    {
        if (landingPoint != null)
        {
            transform.position = new Vector3(landingPoint.position.x, transform.position.y, landingPoint.position.z);
            transform.rotation = landingPoint.rotation;
        }

        if (nextRoute != null)
        {
            this.stopPoint    = nextRoute.stopPoint;
            this.trafficLight = nextRoute.trafficLight;
            this.trafficAgent = nextRoute.trafficAgent;
        }

        this.currentTurnTarget = turnTarget;
        this.state = State.ApproachingQueue;

        // 重置标记，让下一任交警也能正常拿工资
        hasPassedIntersection = false;
        hasPassedExit = false;
    }

    public void Initialize(Transform stop, Transform exit, Transform destroy, TrafficLightBase light, TrafficSignalAgent agent)
    {
        stopPoint    = stop;
        exitPoint    = exit;
        destroyPoint = destroy;
        trafficLight = light;
        trafficAgent = agent;
    }

    // ─────────────────────────────────────────────
    //  Update dispatch
    // ─────────────────────────────────────────────
    void Update()
    {
        switch (state)
        {
            case State.ApproachingQueue: UpdateApproach();  break;
            case State.Queueing:         UpdateQueue();     break;
            case State.PassingIntersection: UpdatePassing(); break;
            case State.CrabWalking:      UpdateCrabWalk();  break;
            case State.Leaving:          UpdateLeaving();  break;
        }
    }

    // ─────────────────────────────────────────────
    //  State Update methods
    // ─────────────────────────────────────────────
    void UpdateApproach()
    {
        if (stopPoint == null) return;

        FaceTarget(stopPoint.position);

        float distToStop = Vector3.Distance(transform.position, stopPoint.position);
        float frontDist  = GetFrontCarDistance();

        bool isQueueHead = distToStop <= GetMinQueueDistanceInLane();
        bool canMove     = frontDist > safeDistance || isQueueHead;

        UpdateSpeed(canMove, frontDist);
        MoveForward();

        if (distToStop < lightCheckDistance)
            state = State.Queueing;
    }

    void UpdateQueue()
    {
        if (stopPoint == null) return;

        float distToStop = Vector3.Distance(transform.position, stopPoint.position);
        float frontDist  = GetFrontCarDistance();
        bool isQueueHead = distToStop <= GetMinQueueDistanceInLane();

        if (isQueueHead)
        {
            if (IsRedOrYellow())
            {
                UpdateSpeed(false, frontDist);
                return;
            }
            else
            {
                // === 【核心修复：完美零延迟加分】 ===
                // 车辆在排队头部，绿灯亮起，决定越过停止线进入路口，立刻当场结算业绩！
                if (!hasPassedIntersection)
                {
                    trafficAgent?.RecordVehiclePassed();
                    hasPassedIntersection = true;
                }
                // ===================================
                state = State.PassingIntersection;
                return;
            }
        }

        bool canMove = frontDist > safeDistance;
        UpdateSpeed(canMove, frontDist);
        MoveForward();
    }

    // =========================
    // 通过路口与离开逻辑 (修复震荡陷阱版)
    // =========================
    void UpdatePassing()
    {
        if (currentTurnTarget != null)
        {
            FaceTarget(currentTurnTarget.position);

            if (Vector3.Distance(transform.position, currentTurnTarget.position) < 0.5f)
            {
                transform.forward = currentTurnTarget.forward;
                currentTurnTarget = null;
                // 仅物理转向，坚决不在路口中心加分，防止直行车漏判！
            }
        }

        float frontDist = GetFrontCarDistance();
        UpdateSpeed(true, frontDist);
        MoveForward();
    }

    void UpdateCrabWalk()
    {
        crabWalkElapsed += Time.deltaTime;
        float t = Mathf.Clamp01(crabWalkElapsed / crabWalkDuration);
        float smoothX = Mathf.Lerp(crabWalkStartX, crabWalkEndX, t);
        transform.localPosition = new Vector3(smoothX, transform.localPosition.y, transform.localPosition.z);

        UpdateSpeed(true, Mathf.Infinity);
        MoveForward();

        if (t >= 1f)
        {
            crabWalkTarget  = null;
            crabWalkStartX  = 0f;
            crabWalkEndX    = 0f;
            crabWalkElapsed = 0f;
            state = State.ApproachingQueue;
        }
    }

    void UpdateLeaving()
    {
        if (destroyPoint == null)
        {
            if (!hasPassedExit)
            {
                hasPassedExit = true; // 延迟奖励切断：离开地图状态与当前交通灯相位无关，禁止产生欺骗性奖励
            }
            Destroy(gameObject);
            return;
        }

        FaceTarget(destroyPoint.position);
        UpdateSpeed(true, Mathf.Infinity);
        MoveForward();

        if (Vector3.Distance(transform.position, destroyPoint.position) < stopDistance + 0.5f)
        {
            if (!hasPassedExit)
            {
                hasPassedExit = true; // 延迟奖励切断：同上
                Destroy(gameObject);
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Speed & movement helpers
    // ─────────────────────────────────────────────
    void UpdateSpeed(bool canMove, float frontDist)
    {
        float targetSpeed = canMove ? desiredSpeed : 0f;

        if (frontDist < safeDistance)
            targetSpeed = Mathf.Min(targetSpeed, Mathf.Max(0f, frontDist - stopDistance));

        if (currentSpeed < targetSpeed)
            currentSpeed += acceleration * Time.deltaTime;
        else
            currentSpeed -= deceleration * Time.deltaTime;

        currentSpeed = Mathf.Clamp(currentSpeed, 0f, desiredSpeed);
    }

    void MoveForward()
    {
        transform.position += transform.forward * currentSpeed * Time.deltaTime;
    }

    void FaceTarget(Vector3 target)
    {
        Vector3 dir = (target - transform.position).normalized;
        if (dir.sqrMagnitude > 0.001f)
            transform.forward = dir;
    }

    // ─────────────────────────────────────────────
    //  Sensor helpers
    // ─────────────────────────────────────────────
    float GetFrontCarDistance()
    {
        if (Physics.Raycast(transform.position + Vector3.up * 0.5f,
                            transform.forward,
                            out RaycastHit hit,
                            safeDistance * 2.5f,
                            carLayer))
            return hit.distance;
        return Mathf.Infinity;
    }

    bool IsRedOrYellow()
    {
        if (trafficLight == null) return false;

        var s = trafficLight.GetState();
        return s == TrafficLightBase.State.Stop ||
               s == TrafficLightBase.State.PrepareToGo ||
               s == TrafficLightBase.State.PrepareToStop;
    }

    float GetMinQueueDistanceInLane()
    {
        return stopDistance + 0.05f;
    }
}
