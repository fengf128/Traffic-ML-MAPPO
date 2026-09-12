using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using System.Collections;

public class TrafficSignalAgent : Agent
{
    [Header("(X,Z)")]
    public int gridX; 
    public int gridZ;
    [Header("Queue Detectors (8 Directional)")]
    public QueueDetector detectorN_Straight;
    public QueueDetector detectorN_Left;
    public QueueDetector detectorS_Straight;
    public QueueDetector detectorS_Left;
    public QueueDetector detectorE_Straight;
    public QueueDetector detectorE_Left;
    public QueueDetector detectorW_Straight;
    public QueueDetector detectorW_Left;

    [Header("Traffic Light Manager")]
    public HealthbarGames.TrafficLightManager trafficLightManager;

    [Header("Phase Settings")]
    public float decisionInterval = 2f;
    private int currentPhase = 0;

    [Header("Reward Hyperparameters (Inspector 可调参)")]
    /// <summary>
    /// 每辆延误车辆每秒的基础惩罚系数（降低至 -0.005，减轻 30 辆车检测区的绝对数值压迫）。
    /// </summary>
    public float queuePenaltyWeight = -0.005f;

    /// <summary>
    /// 每单位累计等待时间每秒的惩罚系数（与基础排队惩罚等宽，作为兜底公平）。
    public float waitTimePenaltyWeight = -0.01f;

    /// <summary>
    /// 相位切换时的惩罚（防止频繁切灯造成路口振荡）。
    /// </summary>
    public float switchPenalty = -0.1f;

    /// <summary>
    /// 单车成功通过路口时的奖励（提升至 0.5，确保放行收益能抵消长检测区的常驻背景惩罚）。
    /// </summary>
    public float throughputReward = 3f;

    /// <summary>
    /// 触发严重拥堵惩罚的排队车辆数阈值（满载约 35 辆，设为 20 辆作为泄压阀）
    /// </summary>
    public float overflowThreshold = 35f; // 从 20f 改为 35f，匹配 30 辆车检测区的实际容量

    /// <summary>
    /// 超过阈值后，超出部分每辆车额外增加的惩罚系数（精准阻击，1 辆溢出的痛感等于抵消 20% 的通过收益）。
    /// </summary>
    public float overflowPenaltyWeight = -0.04f;

    /// <summary>
    /// 记录上一次的动作/相位，用于检测切换事件。
    /// </summary>
    private int previousPhase = -1;

    // =========================================================================
    // 初始化
    // =========================================================================
    public override void Initialize()
    {
        //Time.timeScale = 8f;
        // 👇 魔法破解：绕过 Inspector 面板最大 20 的限制，强行锁定为 100 步
        var requester = GetComponent<Unity.MLAgents.DecisionRequester>();
        if (requester != null)
        {
            requester.DecisionPeriod = 500;// 延长至 10 秒决策周期，扣除 3 秒黄红灯过渡，保留 7 秒纯绿灯时间
        }
    }

    public override void OnEpisodeBegin()
    {
        // ---- 1. 清空场景中所有旧车辆 ----
        CarController[] allCars = FindObjectsOfType<CarController>();
        foreach (CarController car in allCars)
        {
            Destroy(car.gameObject);
        }

        // ---- 2. 重置 Agent 状态 ----
        currentPhase = 0;
        previousPhase = -1;
        SetPhase(currentPhase);

        // ---- 3. 重置 8 个检测器（等待时间/密度/计数全部归零）----
        ResetAllDetectorStats();

        // ---- 4. 重启决策协程 ----
        StopAllCoroutines();
        //StartCoroutine(RequestDecisionLoop());

        // ---- 5. 重置交通流管理器状态（强制前 200 秒均匀车流 + 后续抽取潮汐模式）----
        if (TrafficScenarioManager.Instance != null)
        {
            TrafficScenarioManager.Instance.ResetForNewEpisode();
        }
    }

    // =========================================================================
    // 每帧执行（Debug 用）
    // =========================================================================
    private void Update()
    {
        if (!Application.isPlaying) return;

        if (Input.GetKeyDown(KeyCode.Alpha1)) SetPhase(0);
        if (Input.GetKeyDown(KeyCode.Alpha2)) SetPhase(1);
        if (Input.GetKeyDown(KeyCode.Alpha3)) SetPhase(2);
        if (Input.GetKeyDown(KeyCode.Alpha4)) SetPhase(3);
    }

    // =========================================================================
    // 自动周期触发 RL 决策
    // =========================================================================
    // private IEnumerator RequestDecisionLoop()
    // {
    //     while (true)
    //     {
    //         RequestDecision();
    //         yield return new WaitForSeconds(decisionInterval);
    //     }
    // }

    // =========================================================================
    // RL 动作接收（重构版奖励函数）
    // =========================================================================
    public override void OnActionReceived(ActionBuffers actions)
    {
        int action = actions.DiscreteActions[0];

        // ---- 相位切换惩罚 ----
        if (previousPhase != -1 && action != previousPhase)
        {
            AddReward(switchPenalty);
        }
        previousPhase = action;

        // ---- 执行相位切换 ----
        if (action != currentPhase)
        {
            SetPhase(action);
        }

        // ---- 状态惩罚计算（引入动态溢出惩罚，众生平等，谁堵罚谁） ----
        float[] queues = new float[] {
            detectorN_Straight.currentQueueCount, detectorN_Left.currentQueueCount,
            detectorS_Straight.currentQueueCount, detectorS_Left.currentQueueCount,
            detectorE_Straight.currentQueueCount, detectorE_Left.currentQueueCount,
            detectorW_Straight.currentQueueCount, detectorW_Left.currentQueueCount
        };

        float[] waitTimes = new float[] {
            detectorN_Straight.currentWaitTime, detectorN_Left.currentWaitTime,
            detectorS_Straight.currentWaitTime, detectorS_Left.currentWaitTime,
            detectorE_Straight.currentWaitTime, detectorE_Left.currentWaitTime,
            detectorW_Straight.currentWaitTime, detectorW_Left.currentWaitTime
        };

        float totalQueuePenalty = 0f;
        float totalWaitTimePenalty = 0f;
        float maxWaitLimit = 60f;

        for (int i = 0; i < 8; i++)
        {
            // 1. 基础排队惩罚
            totalQueuePenalty += queues[i] * queuePenaltyWeight;

            // 2. 超过阈值 (7辆) 后的额外溢出惩罚（平滑线性叠加，防梯度爆炸）
            if (queues[i] > overflowThreshold)
            {
                totalQueuePenalty += (queues[i] - overflowThreshold) * overflowPenaltyWeight;
            }

            // 3. 等待时间惩罚（上限截断防异常）
            totalWaitTimePenalty += Mathf.Min(waitTimes[i], maxWaitLimit) * waitTimePenaltyWeight;
        }

        // 合并所有惩罚
        AddReward(totalQueuePenalty + totalWaitTimePenalty);
    }

    // =========================================================================
    // 22 维观测空间
    //   gridX, gridZ            (0-1)   自身身份编码
    //   8 queue counts          (2-9)   各方向排队车辆数 / 40f
    //   8 wait times            (10-17) 各方向累计等待时间 / 30f
    //   4 phase one-hot         (18-21) 红绿灯相位独热编码
    //
    //   总计: 22 个 float
    //   Inspector Space Size  →  22
    // =========================================================================
    public override void CollectObservations(VectorSensor sensor)
    {
        // Send one global telemetry snapshot aligned with this observation.
        if (
            gridX == 0 &&
            gridZ == 0 &&
            TrafficScenarioManager.Instance != null
        )
        {
            TrafficScenarioManager.Instance.SendTelemetrySnapshot();
        }

        // 1. 自身身份编码（帮助 GAT 节点辨识）
        sensor.AddObservation(gridX / 5f);
        sensor.AddObservation(gridZ / 5f);

        // 2. 8 个方向车道的排队车辆数（归一化，最大容量按 40 辆计算）
        sensor.AddObservation(detectorN_Straight.currentQueueCount / 40f);
        sensor.AddObservation(detectorN_Left.currentQueueCount     / 40f);
        sensor.AddObservation(detectorS_Straight.currentQueueCount / 40f);
        sensor.AddObservation(detectorS_Left.currentQueueCount     / 40f);
        sensor.AddObservation(detectorE_Straight.currentQueueCount / 40f);
        sensor.AddObservation(detectorE_Left.currentQueueCount     / 40f);
        sensor.AddObservation(detectorW_Straight.currentQueueCount / 40f);
        sensor.AddObservation(detectorW_Left.currentQueueCount     / 40f);

        // 3. 8 个方向车道的队首等待时间（归一化，与奖励函数的 maxWaitLimit = 60s 严格对齐）
        sensor.AddObservation(Mathf.Clamp01(detectorN_Straight.currentWaitTime / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorN_Left.currentWaitTime     / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorS_Straight.currentWaitTime / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorS_Left.currentWaitTime     / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorE_Straight.currentWaitTime / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorE_Left.currentWaitTime     / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorW_Straight.currentWaitTime / 60f));
        sensor.AddObservation(Mathf.Clamp01(detectorW_Left.currentWaitTime     / 60f));

        // 4. 当前红绿灯相位（独热编码，4 个相位对应 4 个浮点）
        //    Phase 0: NS_Straight/Right green
        //    Phase 1: EW_Straight/Right green
        //    Phase 2: NS_Left green
        //    Phase 3: EW_Left green
        for (int i = 0; i < 4; i++)
            sensor.AddObservation(i == currentPhase ? 1f : 0f);
    }

    // =========================================================================
    // 供外部调用记录车辆通过路口（由 CarController 或 TrafficLightManager
    // 在车辆成功通过时调用，随后 Agent 统一追加吞吐量奖励）
    // =========================================================================
    public void RecordVehiclePassed()
    {
        AddReward(throughputReward);
    }

    // =========================================================================
    // 相位切换（4 个相位）
    // =========================================================================
    private void SetPhase(int phase)
    {
        currentPhase = phase;

        switch (phase)
        {
            case 0:
                trafficLightManager.SetNSGreen();  // 南北直右
                break;
            case 1:
                trafficLightManager.SetEWGreen();  // 东西直右
                break;
            case 2:
                trafficLightManager.SetNSLeftGreen();  // 南北左转
                break;
            case 3:
                trafficLightManager.SetEWLeftGreen();  // 东西左转
                break;
            default:
                break;
        }
    }

    // =========================================================================
    // 重置所有检测器的回合统计
    // =========================================================================
    private void ResetAllDetectorStats()
    {
        detectorN_Straight.ResetEpisodeStats();
        detectorN_Left.ResetEpisodeStats();
        detectorS_Straight.ResetEpisodeStats();
        detectorS_Left.ResetEpisodeStats();
        detectorE_Straight.ResetEpisodeStats();
        detectorE_Left.ResetEpisodeStats();
        detectorW_Straight.ResetEpisodeStats();
        detectorW_Left.ResetEpisodeStats();
    }

    // =========================================================================
    // Heuristic 手动调试（可用键盘控制）
    // =========================================================================
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discrete = actionsOut.DiscreteActions;
        if (Input.GetKey(KeyCode.Alpha1)) discrete[0] = 0;
        else if (Input.GetKey(KeyCode.Alpha2)) discrete[0] = 1;
        else if (Input.GetKey(KeyCode.Alpha3)) discrete[0] = 2;
        else if (Input.GetKey(KeyCode.Alpha4)) discrete[0] = 3;
        else discrete[0] = currentPhase;
    }
}
