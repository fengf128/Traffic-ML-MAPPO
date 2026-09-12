using System;
using Unity.MLAgents.SideChannels;

public class TrafficTelemetrySideChannel : SideChannel
{
    public static readonly Guid ChannelGuid = new Guid(
        "c2b76110-62f5-4a42-a955-59d8c935fa91"
    );

    public TrafficTelemetrySideChannel()
    {
        ChannelId = ChannelGuid;
    }

    protected override void OnMessageReceived(IncomingMessage msg)
    {
        // 当前通道只负责Unity向Python发送遥测数据。
    }

    public void SendSnapshot(
        int seed,
        float simulationTime,
        int stage,
        int scenario,
        int shockwaveState,
        bool hasRolled,
        bool mask0,
        bool mask1,
        bool mask2,
        float phaseDuration,
        float cooldownStartTime,
        TrafficSpawner[] activeSpawners
    )
    {
        using (var msg = new OutgoingMessage())
        {
            // 协议版本，方便以后扩展字段
            msg.WriteInt32(1);

            msg.WriteInt32(seed);
            msg.WriteFloat32(simulationTime);
            msg.WriteInt32(stage);
            msg.WriteInt32(scenario);
            msg.WriteInt32(shockwaveState);
            msg.WriteBoolean(hasRolled);

            msg.WriteBoolean(mask0);
            msg.WriteBoolean(mask1);
            msg.WriteBoolean(mask2);

            msg.WriteFloat32(phaseDuration);
            msg.WriteFloat32(cooldownStartTime);

            int count = activeSpawners == null
                ? 0
                : activeSpawners.Length;

            msg.WriteInt32(count);

            for (int i = 0; i < count; i++)
            {
                TrafficSpawner spawner = activeSpawners[i];

                msg.WriteInt32((int)spawner.myDirection);
                msg.WriteInt32(spawner.laneIndex);

                if (spawner.trafficAgent != null)
                {
                    msg.WriteInt32(spawner.trafficAgent.gridX);
                    msg.WriteInt32(spawner.trafficAgent.gridZ);
                }
                else
                {
                    msg.WriteInt32(-99);
                    msg.WriteInt32(-99);
                }
            }

            QueueMessageToSend(msg);
        }
    }
}