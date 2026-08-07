namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftBlockingStreamTests
{
    [Fact]
    public void CanRead_IsTrue()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void CanWrite_IsTrue()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public void CanSeek_IsFalse()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void Length_Throws()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    [Fact]
    public void Position_Get_Throws()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => stream.Position);
    }

    [Fact]
    public void Position_Set_Throws()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
    }

    [Fact]
    public void Seek_Throws()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void SetLength_Throws()
    {
        using OftBlockingStream stream = new(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => stream.SetLength(10));
    }

    [Fact]
    public void Write_ByteArray_WritesToInnerStream()
    {
        MemoryStream inner = new();
        using OftBlockingStream stream = new(inner);

        byte[] payload = "hello"u8.ToArray();
        stream.Write(payload, 0, payload.Length);

        Assert.Equal(payload, inner.ToArray());
    }

    [Fact]
    public void Write_Span_WritesToInnerStream()
    {
        MemoryStream inner = new();
        using OftBlockingStream stream = new(inner);

        byte[] payload = "hello"u8.ToArray();
        stream.Write((ReadOnlySpan<byte>)payload);

        Assert.Equal(payload, inner.ToArray());
    }

    [Fact]
    public async Task WriteAsync_WritesToInnerStreamAndCompletesSynchronously()
    {
        MemoryStream inner = new();
        using OftBlockingStream stream = new(inner);

        byte[] payload = "hello"u8.ToArray();
        ValueTask task = stream.WriteAsync(payload);

        Assert.True(task.IsCompleted);
        await task;

        Assert.Equal(payload, inner.ToArray());
    }

    [Fact]
    public void Read_ByteArray_ReadsFromInnerStream()
    {
        byte[] payload = "hello"u8.ToArray();
        using OftBlockingStream stream = new(new MemoryStream(payload));

        byte[] buffer = new byte[payload.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public void Read_Span_ReadsFromInnerStream()
    {
        byte[] payload = "hello"u8.ToArray();
        using OftBlockingStream stream = new(new MemoryStream(payload));

        byte[] buffer = new byte[payload.Length];
        int read = stream.Read((Span<byte>)buffer);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task ReadAsync_ReadsFromInnerStreamAndCompletesSynchronously()
    {
        byte[] payload = "hello"u8.ToArray();
        using OftBlockingStream stream = new(new MemoryStream(payload));

        byte[] buffer = new byte[payload.Length];
        ValueTask<int> task = stream.ReadAsync(buffer);

        Assert.True(task.IsCompleted);
        int read = await task;

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public void Flush_FlushesInnerStream()
    {
        Mock<Stream> inner = new();
        inner.Setup(s => s.CanWrite).Returns(true);
        using OftBlockingStream stream = new(inner.Object);

        stream.Flush();

        inner.Verify(s => s.Flush(), Times.Once);
    }

    [Fact]
    public async Task FlushAsync_FlushesInnerStreamAndCompletesSynchronously()
    {
        Mock<Stream> inner = new();
        inner.Setup(s => s.CanWrite).Returns(true);
        using OftBlockingStream stream = new(inner.Object);

        Task task = stream.FlushAsync();

        Assert.True(task.IsCompleted);
        await task;

        inner.Verify(s => s.Flush(), Times.Once);
    }

    [Fact]
    public void Dispose_DoesNotDisposeInnerStream()
    {
        MemoryStream inner = new();
        OftBlockingStream stream = new(inner);

        stream.Dispose();

        // The inner stream is not owned by OftBlockingStream, so it must still be usable afterward.
        inner.WriteByte(1);
    }
}
