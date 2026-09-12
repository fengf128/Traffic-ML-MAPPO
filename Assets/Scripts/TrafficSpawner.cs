using UnityEngine;
using System.Collections;

public enum SpawnerDirection { North, South, East, West }

public class TrafficSpawner : MonoBehaviour
{
    public GameObject carPrefab;
    private Coroutine spawnCoroutine;
    private System.Random evaluationRandom;
    private bool useEvaluationRandom = false;
    private int stableSpawnerIndex = -1;

    // 评估模式下，出生点被占用时保留当前车辆并按固定间隔重试。
    private const float blockedRetryInterval = 0.5f;

    [Header("Spawn timing")]
    public float minSpawnInterval = 3f;
    public float maxSpawnInterval = 7f;

    [Header("Spawner Direction")]
    [Tooltip("当前出生点的物理方位，用于潮汐车流计算")]
    public SpawnerDirection myDirection;

    [Header("Lane Index")]
    [Tooltip("当前出生点属于该方向的第几条车道 (0, 1, 2)")]
    public int laneIndex = 0;

    [Header("Global destination grid (e.g. new Vector2Int(0,2), new Vector2Int(2,1))")]
    public Vector2Int[] validDestinations;

    [Header("Overlap check")]
    public LayerMask carLayer;

    [Header("RL Agent Reference (for throughput reward)")]
    public TrafficSignalAgent trafficAgent;

    [Header("Initial Route for spawned cars")]
    public RouteConfig initialRoute;

    void Start()
    {
        // 如果全局重置已经提前启动协程，避免重复启动
        if (spawnCoroutine == null)
        {
            spawnCoroutine = StartCoroutine(SpawnLoop());
        }
    }

    /// <summary>
    /// 每个新Episode重新启动发车流程。
    /// 停止上一局未完成的WaitForSeconds，重新计算本局第一次发车时间。
    /// </summary>
    public void RestartForNewEpisode(
        int episodeSeed,
        int spawnerIndex,
        bool evaluationMode
    )
    {
        stableSpawnerIndex = spawnerIndex;
        useEvaluationRandom = evaluationMode;

        if (useEvaluationRandom)
        {
            int spawnerSeed;
            unchecked
            {
                // 乘1000避免相邻Episode和相邻Spawner产生相同种子。
                spawnerSeed = episodeSeed * 1000 + stableSpawnerIndex;
            }

            evaluationRandom = new System.Random(spawnerSeed);
        }
        else
        {
            evaluationRandom = null;
        }

        if (spawnCoroutine != null)
        {
            StopCoroutine(spawnCoroutine);
            spawnCoroutine = null;
        }

        if (isActiveAndEnabled)
        {
            spawnCoroutine = StartCoroutine(SpawnLoop());
        }
    }

    // 保留无参数接口，避免场景中其他旧调用失效。
    public void RestartForNewEpisode()
    {
        RestartForNewEpisode(0, -1, false);
    }

    private void OnDisable()
    {
        if (spawnCoroutine != null)
        {
            StopCoroutine(spawnCoroutine);
            spawnCoroutine = null;
        }
    }

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            if (useEvaluationRandom)
            {
                if (validDestinations == null || validDestinations.Length == 0)
                {
                    Debug.LogWarning(
                        $"[{name}] validDestinations is empty. No car spawned."
                    );
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                // 每次发车尝试先固定随机间隔和目的地。
                // 后续出生点是否拥堵，不再改变随机数的消耗顺序。
                float random01 = (float)evaluationRandom.NextDouble();
                float baseInterval = Mathf.Lerp(
                    minSpawnInterval,
                    maxSpawnInterval,
                    random01
                );

                int destinationIndex = evaluationRandom.Next(
                    0,
                    validDestinations.Length
                );
                Vector2Int destination =
                    validDestinations[destinationIndex];

                float multiplier =
                    TrafficScenarioManager.Instance
                        .GetSpawnIntervalMultiplier(
                            myDirection,
                            laneIndex
                        );

                yield return new WaitForSeconds(
                    baseInterval * multiplier
                );

                // 出生点堵塞时保留同一辆车和同一目的地，
                // 重试期间不再抽取任何新随机数。
                while (!TrySpawnPreparedCar(destination))
                {
                    yield return new WaitForSeconds(
                        blockedRetryInterval
                    );
                }

                continue;
            }

            // 训练模式保留原来的全局随机和发车失败逻辑。
            float trainingBaseInterval = Random.Range(
                minSpawnInterval,
                maxSpawnInterval
            );

            float trainingMultiplier =
                TrafficScenarioManager.Instance
                    .GetSpawnIntervalMultiplier(
                        myDirection,
                        laneIndex
                    );

            yield return new WaitForSeconds(
                trainingBaseInterval * trainingMultiplier
            );

            SpawnCar();
        }
    }

    void SpawnCar()
    {
        if (validDestinations == null || validDestinations.Length == 0)
        {
            Debug.LogWarning($"[{name}] validDestinations is empty. No car spawned.");
            return;
        }

        if (Physics.Raycast(transform.position + Vector3.up * 0.5f,
                            transform.forward,
                            out RaycastHit hit,
                            2f,
                            carLayer))
        {
            return;
        }

        Vector2Int destination = validDestinations[
            Random.Range(0, validDestinations.Length)
        ];
        SpawnCarWithDestination(destination);
    }

    private bool TrySpawnPreparedCar(Vector2Int destination)
    {
        if (Physics.Raycast(transform.position + Vector3.up * 0.5f,
                            transform.forward,
                            out RaycastHit hit,
                            2f,
                            carLayer))
        {
            return false;
        }

        SpawnCarWithDestination(destination);
        return true;
    }

    private void SpawnCarWithDestination(Vector2Int destination)
    {
        int targetGridX = destination.x;
        int targetGridZ = destination.y;

        Transform spawnTurnTarget = null;
        if (initialRoute != null)
        {
            if (initialRoute.laneType == LaneType.LeftOnly)
            {
                spawnTurnTarget = initialRoute.leftTarget;
            }
            else
            {
                int myX = trafficAgent.gridX;
                int myZ = trafficAgent.gridZ;
                Vector3 currentForward = transform.forward;
                int diffX = targetGridX - myX;
                int diffZ = targetGridZ - myZ;

                bool isHorizontalExit = (targetGridX < 0 || targetGridX > 2);
                bool isVerticalExit  = (targetGridZ < 0 || targetGridZ > 2);

                Vector3 desiredDir = currentForward;
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

                float angle = Vector3.SignedAngle(currentForward, desiredDir, Vector3.up);

                if (angle < -45f)
                    spawnTurnTarget = initialRoute.straightTarget;
                else if (angle > 45f)
                    spawnTurnTarget = initialRoute.rightTarget;
                else
                    spawnTurnTarget = initialRoute.straightTarget;
            }
        }

        GameObject carObj = Instantiate(carPrefab, transform.position, transform.rotation);
        CarController car = carObj.GetComponent<CarController>();
        car.InitializeGlobal(targetGridX, targetGridZ, initialRoute, spawnTurnTarget);
    }
}
