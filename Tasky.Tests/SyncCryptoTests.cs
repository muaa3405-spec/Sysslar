using System.Text;
using Tasky.Services.Sync;
using Xunit;

namespace Tasky.Tests;

public sealed class SyncCryptoTests
{
    [Fact]
    public void The_key_code_round_trips_and_forgives_case_dashes_and_lookalike_digits()
    {
        var key = SyncKey.Generate();
        var code = key.ToCode();

        Assert.Equal(9, code.Split('-').Length);
        Assert.Equal(key.FingerprintHex, SyncKey.TryParseCode(code)!.FingerprintHex);
        Assert.Equal(key.FingerprintHex, SyncKey.TryParseCode(code.ToLowerInvariant().Replace("-", " "))!.FingerprintHex);
        Assert.Equal(key.FingerprintHex, SyncKey.TryParseCode(code.Replace('O', '0').Replace('I', '1'))!.FingerprintHex);
    }

    [Fact]
    public void A_typo_in_the_key_code_is_caught()
    {
        var code = SyncKey.Generate().ToCode();
        var typo = (code[0] == 'A' ? "B" : "A") + code[1..];
        Assert.Null(SyncKey.TryParseCode(typo));
        Assert.Null(SyncKey.TryParseCode(code[..^3]));
        Assert.Null(SyncKey.TryParseCode(""));
    }

    [Fact]
    public void A_sealed_file_opens_only_with_the_same_key_scope_kind_and_place()
    {
        var key = SyncKey.Generate();
        var data = Encoding.UTF8.GetBytes("my private tasks");
        var file = SyncCrypto.Seal(key, "ws1", SyncFileKind.Snapshot, "snapshot|ws1|dev1", data);

        Assert.Equal(data, SyncCrypto.Open(key, "ws1", SyncFileKind.Snapshot, "snapshot|ws1|dev1", file));
        Assert.Throws<SyncFileException>(() => SyncCrypto.Open(SyncKey.Generate(), "ws1", SyncFileKind.Snapshot, "snapshot|ws1|dev1", file));
        Assert.Throws<SyncFileException>(() => SyncCrypto.Open(key, "ws2", SyncFileKind.Snapshot, "snapshot|ws1|dev1", file));
        Assert.Throws<SyncFileException>(() => SyncCrypto.Open(key, "ws1", SyncFileKind.Device, "snapshot|ws1|dev1", file));
        Assert.Throws<SyncFileException>(() => SyncCrypto.Open(key, "ws1", SyncFileKind.Snapshot, "snapshot|ws1|dev2", file));
    }

    [Fact]
    public void An_edited_or_cut_off_file_is_refused()
    {
        var key = SyncKey.Generate();
        var file = SyncCrypto.Seal(key, "s", SyncFileKind.Blob, "c", new byte[1000]);

        var edited = file.ToArray();
        edited[500] ^= 1;
        Assert.Throws<SyncFileException>(() => SyncCrypto.Open(key, "s", SyncFileKind.Blob, "c", edited));
        Assert.Throws<SyncFileException>(() => SyncCrypto.Open(key, "s", SyncFileKind.Blob, "c", file[..^10]));
    }

    [Fact]
    public void The_file_reveals_nothing_but_the_key_fingerprint()
    {
        var key = SyncKey.Generate();
        var secret = "Buy a ring for Sam";
        var file = SyncCrypto.SealJson(key, "s", SyncFileKind.Snapshot, "c", new { title = secret });

        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(file));
        Assert.Equal(key.FingerprintHex, SyncCrypto.ReadFingerprint(file));
        Assert.Equal(secret, SyncCrypto.OpenJson<Dictionary<string, string>>(key, "s", SyncFileKind.Snapshot, "c", file)["title"]);
    }

    [Fact]
    public void Base32_round_trips()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(bytes, SyncIds.FromBase32(SyncIds.Base32(bytes))![..32]);
        Assert.Matches("^[a-z2-7]{26}$", SyncIds.New());
    }
}
