using System.Collections.Generic;
using UnityEngine;

public class QueueDetector : MonoBehaviour
{
    // =========================================================================
    // SW-GAT / TA-GAT 特征提取
    // =========================================================================
    [Header("SW-GAT / TA-GAT 状态特征（Inspector 实时查看）")]
    /// <summary>
    /// 当前车道累积延误时间（排队积分法，仅统计减速/停车的车辆）。
    /// </summary>
    public float currentWaitTime = 0f;

    /// <summary>
    /// 当前车道流量密度（0~1），由排队数量除以车道最大容量得出。
    /// </summary>
    public float flowDensity = 0f;

    /// <summary>
    /// 车道最大容量（辆），用于归一化密度。
    /// </summary>
    public float maxLaneCapacity = 10f;

    /// <summary>
    /// 正常行驶速度阈值（m/s），设为最高速度 5 的一半。
    /// 低于此速度严格视为因拥堵/红灯造成的延误。
    /// </summary>
    public float movingSpeedThreshold = 2.5f;

    [Header("Debug / Inspector Display")]
    /// <summary>
    /// 检测框内第一辆车的真实速度，供开发者在 Inspector 中精准校准，
    /// 避免多车平均值带来的数值污染。
    /// </summary>
    public float debugFirstCarSpeed = 0f;

    /// <summary>
    /// 当前检测框内处于延误状态的车辆数（速度 < 阈值）。
    /// </summary>
    public int currentQueueCount = 0;

    /// <summary>
    /// 本回合内通过检测框的车辆总数（供 Agent 计算吞吐量奖励）。
    /// </summary>
    private int passedCarsThisEpisode = 0;

    /// <summary>
    /// 直接追踪检测区内的 CarController 脚本组件，
    /// 读取其原生 CurrentSpeed，完全绕过位置差值算法。
    /// </summary>
    private List<CarController> trackedCars = new List<CarController>();

    /// <summary>
    /// 已通过计数只读属性，外部（Agent）通过此接口查询。
    /// </summary>
    public int PassedCarsThisEpisode => passedCarsThisEpisode;

    // =========================================================================
    // 碰撞进入
    // =========================================================================
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Car"))
            return;

        CarController car = other.GetComponentInParent<CarController>();
        if (car != null && !trackedCars.Contains(car))
            trackedCars.Add(car);
    }

    // =========================================================================
    // 碰撞离开
    // =========================================================================
    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag("Car"))
            return;

        CarController car = other.GetComponentInParent<CarController>();
        if (car != null)
            trackedCars.Remove(car);
    }

    // =========================================================================
    // 每帧更新（直接读取 CarController.CurrentSpeed，消除帧率抖动）
    // =========================================================================
    private void Update()
    {
        // ---- 第一步：清理已销毁的车辆引用 ----
        trackedCars.RemoveAll(car => car == null);

        // ---- 第二步：重置临时变量 ----
        int delayedCarsCount = 0;
        debugFirstCarSpeed = 0f;

        // ---- 第三步：遍历追踪列表，直接读取原生速度 ----
        bool firstFound = false;
        foreach (CarController car in trackedCars)
        {
            float speed = car.CurrentSpeed;

            if (speed < movingSpeedThreshold)
                delayedCarsCount++;

            if (!firstFound)
            {
                debugFirstCarSpeed = speed;
                firstFound = true;
            }
        }

        // ---- 第四步：同步当前延误数 ----
        currentQueueCount = delayedCarsCount;

        // ---- 第五步：等待时间重构 —— 仅统计队首车辆，消除陈旧数据污染 ----
        if (delayedCarsCount > 0 && debugFirstCarSpeed < movingSpeedThreshold)
        {
            currentWaitTime += Time.deltaTime; // 队首车仍在延误，累加
        }
        else
        {
            currentWaitTime = 0f; // 队首车动了，等待时间清零，消除死亡螺旋
        }

        // ---- 第六步：密度计算与清零 ----
        flowDensity = Mathf.Clamp01((float)trackedCars.Count / maxLaneCapacity);
        if (trackedCars.Count == 0)
            currentWaitTime = 0f;
    }

    // =========================================================================
    // 供外部调用记录车辆通过（由 TrafficSignalAgent 在车辆驶出路口时调用，
    // 随后 Agent 在其 OnActionReceived 中统一计算吞吐量奖励）
    // =========================================================================
    public void RecordVehiclePassed()
    {
        passedCarsThisEpisode++;
    }

    // =========================================================================
    // Episode 重置（由 TrafficSignalAgent 在 OnEpisodeBegin 调用）
    // =========================================================================
    public void ResetEpisodeStats()
    {
        passedCarsThisEpisode = 0;
        currentWaitTime = 0f;
        flowDensity = 0f;
        currentQueueCount = 0;
        debugFirstCarSpeed = 0f;
    }
}
