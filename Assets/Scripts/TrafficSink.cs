using UnityEngine;

public class TrafficSink : MonoBehaviour
{
    [Header("网格坐标 (范围 -1 到 3)")]
    public int gridX;
    public int gridZ;

    private void OnTriggerEnter(Collider other)
    {
        // 碰触发检 检查是否是车辆
        CarController car = other.GetComponent<CarController>();
        if (car != null)
        {
            // 学习回调 统计累计
            // 通知之前注册的可视化系统或统计图表的地点
            if (car.targetGridX == this.gridX && car.targetGridZ == this.gridZ)
            {
                // 目的地对了！未来如果要画路径统计图，这里可以加个更安全的计数器
                // Debug.Log($"[成功] 车辆按设定路线从出口离开了 ({gridX}, {gridZ})");
            }
            else
            {
                // 走错路了，调试日志方便帮忙捉 Bug
                Debug.LogWarning($"[警告] 车辆走错路！目标({car.targetGridX}, {car.targetGridZ})，实际出口({gridX}, {gridZ})");
            }

            // 销毁车辆，释放内存
            Destroy(other.gameObject);
        }
    }
}
