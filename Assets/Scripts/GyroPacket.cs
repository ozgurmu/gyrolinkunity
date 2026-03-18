using System;
using UnityEngine;

[Serializable]
public class GyroPacket
{
    public float x;
    public float y;
    public float z;
    public float w;
    public long timestamp;

    public GyroPacket()
    {
    }

    public GyroPacket(Quaternion rotation, long unixTimeMilliseconds)
    {
        x = rotation.x;
        y = rotation.y;
        z = rotation.z;
        w = rotation.w;
        timestamp = unixTimeMilliseconds;
    }

    public Quaternion ToQuaternion()
    {
        return new Quaternion(x, y, z, w);
    }
}
