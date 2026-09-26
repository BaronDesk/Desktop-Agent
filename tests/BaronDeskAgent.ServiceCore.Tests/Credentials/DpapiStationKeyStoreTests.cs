using System.Security.Cryptography;
using BaronDeskAgent.ServiceCore.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Tests.Credentials;

public sealed class DpapiStationKeyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"barondesk-key-{Guid.NewGuid():N}");
    private readonly string _path;

    public DpapiStationKeyStoreTests()
    {
        _path = Path.Combine(_directory, "station.key");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_key_pair_is_generated_once_and_survives_a_restart()
    {
        using var first = CreateStore().GetOrCreate();
        using var afterRestart = CreateStore().GetOrCreate();

        Assert.Equal(256, first.KeySize);
        Assert.Equal(PublicKey(first), PublicKey(afterRestart));
    }

    [Fact]
    public void The_private_key_is_never_on_disk_in_plain_text()
    {
        using var key = CreateStore().GetOrCreate();

        var privateKey = key.ExportPkcs8PrivateKey();
        var content = File.ReadAllBytes(_path);
        Assert.Equal(-1, content.AsSpan().IndexOf(privateKey));
    }

    [Fact]
    public void An_unreadable_key_is_replaced_by_a_new_identity()
    {
        using var original = CreateStore().GetOrCreate();

        // e.g. a disk image cloned from another station: its DPAPI blob does not decrypt here.
        File.WriteAllBytes(_path, [1, 2, 3, 4]);
        using var replacement = CreateStore().GetOrCreate();

        Assert.NotEqual(PublicKey(original), PublicKey(replacement));
    }

    [Fact]
    public void Delete_forgets_the_identity()
    {
        var store = CreateStore();
        using var original = store.GetOrCreate();

        Assert.True(store.Delete());
        Assert.False(store.Delete());

        using var fresh = store.GetOrCreate();
        Assert.NotEqual(PublicKey(original), PublicKey(fresh));
    }

    private DpapiStationKeyStore CreateStore() => new(_path, NullLogger<DpapiStationKeyStore>.Instance);

    private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
}
