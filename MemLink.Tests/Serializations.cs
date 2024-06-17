//using MemoryPack;
//using Xunit;
//using System.Diagnostics.CodeAnalysis;
//using System.ComponentModel;

//namespace MemLink.Tests;

//public class Serializations
//{
//    [Fact]
//    public void TestSerialization()
//    {
//        Request<string> request = new("Test") { ServiceOperationId = Guid.NewGuid() };

//        var bytes = ToRequestBytes(request);

//        FromRequestBytes<string>(bytes, out var guid, out var data);

//        Assert.Equal(request.ServiceOperationId, guid);

//        Assert.Equal(request.Data, MemoryPackSerializer.Deserialize<string>(data));
//    }

//    public static Span<byte> ToRequestBytes<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(Request<T> request)
//    {
//        Span<byte> bytes = MemoryPackSerializer.Serialize(request);

//        byte[] lenBytes = BitConverter.GetBytes(bytes.Length - 21);

//        for (int i = 17; i < 21; i++) bytes[i] = lenBytes[i - 17];

//        return bytes;
//    }

//    public static void FromRequestBytes<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(Span<byte> bytes, out Guid guid, out ReadOnlySpan<byte> data)
//    {
//        guid = new(bytes.Slice(1, 16));

//        int len = BitConverter.ToInt32(bytes.Slice(17, 4));

//        data = bytes.Slice(21, len);
//    }
//}

//[MemoryPackable]
//public partial struct Request<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T data) : IMessage<T>
//{
//    [MemoryPackOrder(0)]
//    public Guid ServiceOperationId { get; init; }
//    [MemoryPackOrder(1)]
//    public int Length { get; set; }
//    [MemoryPackOrder(2)]
//    public T Data { get; } = data;

//    public static implicit operator T(Request<T> e) => e.Data;
//    public static implicit operator Request<T>((Guid id, T data) from) => new(from.data) { ServiceOperationId = from.id };
//}
//[MemoryPackable]
//public partial struct Response<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ResponseStatus status, T data) : IMessage<T> where T : struct
//{
//    [MemoryPackOrder(0)]
//    public int Length { get; set; }

//    [MemoryPackOrder(2)]
//    public T Data { get; } = data;

//    [MemoryPackOrder(3)]
//    public ResponseStatus Status { get; } = status;

//    public static implicit operator (T data, ResponseStatus status)(Response<T> e) => (e.Data, e.Status);
//    public static implicit operator Response<T>((T data, ResponseStatus status) e) => new(e.status, e.data);
//}

//public interface IMessage<T>
//{
//    public int Length { get; set; }
//    T Data { get; }
//}

//public enum ResponseStatus
//{
//    Success,
//    Failed
//}