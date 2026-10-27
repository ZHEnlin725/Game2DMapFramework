using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public interface ISerializable
{
    void Serialize(ReadWriteStream stream);

    void Deserialize(ReadWriteStream stream);
}

public class ReadWriteStream : IDisposable
{
    [StructLayout(LayoutKind.Explicit)]
    private struct SingleHelper
    {
        [FieldOffset(0)] private int int32;
        [FieldOffset(0)] private float single;

        public static float ReadSingle(ReadWriteStream stream) =>
            new SingleHelper { int32 = stream.ReadInt32() }.single;

        public static float ReadSingle(ReadWriteStream stream, ref int index) =>
            new SingleHelper { int32 = stream.ReadInt32(ref index) }.single;

        public static void WriteSingle(ReadWriteStream stream, float value) =>
            stream.WriteInt32(new SingleHelper { single = value }.int32);
    }

    private int position;
    private byte[] bytes;

    public int Position => position;

    public int Length => bytes.Length;

    public ReadWriteStream(int capacity = 32)
    {
        bytes = new byte[Math.Max(1, capacity)];
    }

    public ReadWriteStream(byte[] buffer, int position = 0)
    {
        this.bytes = buffer;
        this.position = position;
    }

    public bool CanRead => position < bytes.Length;

    public void SetPosition(int val)
    {
        position = val;
    }

    public void WriteByte(byte value)
    {
        EnsureCapacity(position + 1);
        bytes[position++] = value;
    }

    public void WriteBoolean(bool value) =>
        WriteByte((byte)(value ? 1 : 0));

    public void WriteInt16(short value)
    {
        for (var i = 0; i < 16; i += 8)
            WriteByte((byte)((value >> i) & 0xff));
    }

    public void WriteInt32(int value, bool optimize = false)
    {
        byte byteNums;
        if (optimize)
        {
            byteNums = 0;
            for (var i = 24; i >= 0; i -= 8) //24 16 8  0 
                if ((byte)((value >> i) & 0xff) != 0)
                {
                    byteNums = (byte)((i + 8) / 8);
                    break;
                }

            WriteByte(byteNums);
        }
        else
        {
            byteNums = 4;
        }

        for (var i = 0; i < byteNums; i++)
            WriteByte((byte)((value >> (i * 8)) & 0xff));
    }

    public void WriteInt64(long value)
    {
        for (var i = 0; i < 64; i += 8)
            WriteByte((byte)((value >> i) & 0xff));
    }

    public void WriteString(string str)
    {
        if (!string.IsNullOrEmpty(str))
        {
            var bytes = Encoding.UTF8.GetBytes(str);
            var cnt = bytes?.Length ?? 0;
            WriteInt32(cnt);
            for (int i = 0; i < cnt; i++)
                WriteByte(bytes[i]);
        }
        else
        {
            WriteInt32(0);
        }
    }

    public void WriteBytes(byte[] bytes)
    {
        var length = bytes?.Length ?? 0;
        WriteInt32(length);
        for (var i = 0; i < length; i++)
            WriteByte(bytes[i]);
    }

    public void WriteArray(int[] array, bool optimize = false)
    {
        var length = array?.Length ?? 0;
        WriteInt32(length, optimize);
        for (int i = 0; i < length; i++)
            WriteInt32(array[i], optimize);
    }

    public void WriteArray(Vector2[] array)
    {
        var length = array?.Length ?? 0;
        WriteInt32(length);
        for (int i = 0; i < length; i++)
            WriteVector2(array[i]);
    }

    public void WriteArray(Vector3[] array)
    {
        var length = array?.Length ?? 0;
        WriteInt32(length);
        for (int i = 0; i < length; i++)
            WriteVector3(array[i]);
    }

    public void WriteArray(bool[] array)
    {
        var length = array?.Length ?? 0;
        WriteInt32(length);
        for (int i = 0; i < length; i++)
            WriteByte((byte)(array[i] ? 1 : 0));
    }

    public void WriteArray(float[] array)
    {
        var length = array?.Length ?? 0;
        WriteInt32(length);
        for (int i = 0; i < length; i++)
            WriteSingle(array[i]);
    }

    public void WriteSingle(float value) =>
        SingleHelper.WriteSingle(this, value);

    public void WriteVector2(Vector2 vector2)
    {
        for (int i = 0; i < 2; i++)
            WriteSingle(vector2[i]);
    }

    public void WriteVector3(Vector3 vector3)
    {
        for (int i = 0; i < 3; i++)
            WriteSingle(vector3[i]);
    }

    public void WriteMatrix4x4(Matrix4x4 matrix4X4)
    {
        for (int i = 0; i < 16; i++)
            WriteSingle(matrix4X4[i]);
    }

    public void WriteVector2Int(Vector2Int vector2Int)
    {
        for (int i = 0; i < 2; i++)
            WriteInt32(vector2Int[i]);
    }

    public void WriteVector3Int(Vector3Int vector3Int)
    {
        for (int i = 0; i < 3; i++)
            WriteInt32(vector3Int[i]);
    }

    public void WriteRectInt(RectInt rectInt)
    {
        WriteVector2Int(rectInt.position);
        WriteVector2Int(rectInt.size);
    }

    public int ReadByte(ref int index) =>
        bytes[index++];

    public bool ReadBoolean(ref int index) =>
        (byte)ReadByte(ref index) == (byte)1;

    public short ReadInt16(ref int index)
    {
        short result = 0;
        for (var i = 0; i < 16; i += 8)
            result |= (short)(ReadByte(ref index) << i);
        return result;
    }

    public int ReadInt32(ref int index, bool optimize = false)
    {
        var result = 0;
        var byteNums = (byte)(optimize ? ReadByte(ref index) : 4);
        for (var i = 0; i < byteNums; i++)
            result |= ((int)ReadByte(ref index)) << (i * 8);
        return result;
    }

    public long ReadInt64(ref int index)
    {
        var result = 0L;
        for (var i = 0; i < 64; i += 8)
            result |= (long)ReadByte(ref index) << i;
        return result;
    }

    public string ReadString(ref int index)
    {
        var result = string.Empty;
        var cnt = ReadInt32(ref index);
        if (cnt > 0)
        {
            result = Encoding.UTF8.GetString(this.bytes, (int)index, cnt);
            // this.Seek(cnt, SeekOrigin.Current);
            index += cnt;
        }

        return result;
    }

    public float ReadSingle(ref int index) =>
        SingleHelper.ReadSingle(this, ref index);

    public Vector2 ReadVector2(ref int index)
    {
        Vector2 vector2 = default;
        for (int i = 0; i < 2; i++)
            vector2[i] = ReadSingle(ref index);
        return vector2;
    }

    public Vector3 ReadVector3(ref int index)
    {
        Vector3 vector3 = default;
        for (int i = 0; i < 3; i++)
            vector3[i] = ReadSingle(ref index);
        return vector3;
    }

    public Matrix4x4 ReadMatrix4x4(ref int index)
    {
        Matrix4x4 matrix4X4 = default;
        for (int i = 0; i < 16; i++)
            matrix4X4[i] = ReadSingle(ref index);
        return matrix4X4;
    }

    public Vector2Int ReadVector2Int(ref int index)
    {
        Vector2Int vector2Int = default;
        for (int i = 0; i < 2; i++)
            vector2Int[i] = ReadInt32(ref index);
        return vector2Int;
    }

    public Vector3Int ReadVector3Int(ref int index)
    {
        Vector3Int vector3Int = default;
        for (int i = 0; i < 3; i++)
            vector3Int[i] = ReadInt32(ref index);
        return vector3Int;
    }

    public RectInt ReadRectInt(ref int index)
    {
        var position = ReadVector2Int(ref index);
        var size = ReadVector2Int(ref index);
        return new RectInt(position, size);
    }

    public byte[] ReadBytes(ref int index)
    {
        var length = ReadInt32(ref index);
        var bytes = (byte[])default;
        if (length > 0)
        {
            bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = (byte)ReadByte(ref index);
        }

        return bytes;
    }

    public int[] ReadArrayInt(ref int index, bool optimize = false)
    {
        var length = ReadInt32(ref index, optimize);
        if (length > 0)
        {
            var array = new int[length];
            for (int i = 0; i < length; i++)
                array[i] = ReadInt32(ref index, optimize);

            return array;
        }

        return null;
    }

    public Vector2[] ReadArrayVector2(ref int index)
    {
        var length = ReadInt32(ref index);
        if (length > 0)
        {
            var array = new Vector2[length];
            for (int i = 0; i < length; i++)
                array[i] = ReadVector2(ref index);

            return array;
        }

        return null;
    }

    public Vector3[] ReadArrayVector3(ref int index)
    {
        var length = ReadInt32(ref index);
        if (length > 0)
        {
            var array = new Vector3[length];
            for (int i = 0; i < length; i++)
                array[i] = ReadVector3(ref index);

            return array;
        }

        return null;
    }

    public bool[] ReadArrayBool(ref int index)
    {
        var length = ReadInt32(ref index);
        if (length > 0)
        {
            var array = new bool[length];
            for (int i = 0; i < length; i++)
                array[i] = ReadByte(ref index) != 0;

            return array;
        }

        return null;
    }

    public int ReadByte() =>
        bytes[position++];

    public bool ReadBoolean() =>
        ReadBoolean(ref this.position);

    public short ReadInt16() =>
        ReadInt16(ref this.position);

    public int ReadInt32(bool optimize = false) =>
        ReadInt32(ref this.position, optimize);

    public long ReadInt64()
    {
        var result = 0L;
        for (var i = 0; i < 64; i += 8)
            result |= (long)ReadByte() << i;
        return result;
    }

    public string ReadString()
    {
        var result = string.Empty;
        var cnt = ReadInt32();
        if (cnt > 0)
        {
            result = Encoding.UTF8.GetString(this.bytes, (int)this.position, cnt);
            // this.Seek(cnt, SeekOrigin.Current);
            this.position += cnt;
        }

        return result;
    }

    public float ReadSingle() =>
        SingleHelper.ReadSingle(this, ref this.position);

    public Vector2 ReadVector2() =>
        ReadVector2(ref this.position);

    public Vector3 ReadVector3() =>
        ReadVector3(ref this.position);

    public Matrix4x4 ReadMatrix4x4() =>
        ReadMatrix4x4(ref this.position);

    public Vector2Int ReadVector2Int() =>
        ReadVector2Int(ref this.position);

    public Vector3Int ReadVector3Int() =>
        ReadVector3Int(ref this.position);

    public RectInt ReadRectInt()
    {
        var position = ReadVector2Int(ref this.position);
        var size = ReadVector2Int(ref this.position);
        return new RectInt(position, size);
    }

    public byte[] ReadBytes() =>
        ReadBytes(ref this.position);

    public int[] ReadArrayInt(bool optimize = false) =>
        ReadArrayInt(ref this.position, optimize);

    public Vector2[] ReadArrayVector2() =>
        ReadArrayVector2(ref this.position);

    public Vector3[] ReadArrayVector3() =>
        ReadArrayVector3(ref this.position);

    public bool[] ReadArrayBool() =>
        ReadArrayBool(ref this.position);

    public byte[] ToArray() => bytes;

    private void EnsureCapacity(int length)
    {
        var bytesLength = bytes.Length;
        while (bytesLength < length)
            bytesLength = (bytesLength + 1) << 1;
        var buffer = new byte[bytesLength];
        Array.Copy(bytes, buffer, bytes.Length);
        bytes = buffer;
    }

    public void Dispose()
    {
        bytes = null;
    }

    public static T Deserialize<T>(byte[] bytes) where T : ISerializable, new()
    {
        using var stream = new ReadWriteStream(bytes);
        var inst = new T();
        inst.Deserialize(stream);
        return inst;
    }

    public static byte[] Serialize<T>(T inst) where T : ISerializable
    {
        using var stream = new ReadWriteStream();
        inst.Serialize(stream);
        return stream.ToArray();
    }

    public void Write<T>(T serializable) where T : ISerializable =>
        serializable.Serialize(this);

    public T Read<T>() where T : ISerializable, new()
    {
        var serializable = new T();
        serializable.Deserialize(this);
        return serializable;
    }

    public T Read<T>(Func<T> factory) where T : ISerializable
    {
        var inst = factory();
        inst.Deserialize(this);
        return inst;
    }

    public void WriteArray<T>(T[] array) where T : ISerializable
    {
        var length = array?.Length ?? 0;
        this.WriteInt32(length);
        for (int i = 0; i < length; i++)
            array[i].Serialize(this);
    }

    public T[] ReadArray<T>() where T : ISerializable, new()
    {
        var length = this.ReadInt32();
        T[] array = null;
        if (length > 0)
        {
            array = new T[length];
            for (int i = 0; i < length; i++)
            {
                var inst = new T();
                inst.Deserialize(this);
                array[i] = inst;
            }
        }

        return array;
    }

    public void WriteList<T>(List<T> list) where T : ISerializable
    {
        var length = list?.Count ?? 0;
        this.WriteInt32(length);
        for (int i = 0; i < length; i++)
            list[i].Serialize(this);
    }

    public List<T> ReadList<T>() where T : ISerializable, new()
    {
        var length = this.ReadInt32();
        List<T> list = null;
        if (length > 0)
        {
            list = new List<T>(length);
            for (var i = 0; i < length; i++)
            {
                var inst = new T();
                inst.Deserialize(this);
                list.Add(inst);
            }
        }

        return list;
    }
}