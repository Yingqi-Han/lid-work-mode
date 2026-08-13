using System.Runtime.Serialization;
using System.Text;
using Xunit;

namespace LidWorkMode.Tests;

public sealed class GuardStateTests
{
    private static readonly Guid Scheme = Guid.Parse("4b607d49-78c4-4638-b1f7-4ca7e74b0383");
    private static readonly DateTime CreatedUtc = new(2026, 8, 13, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void RoundTrip_PreservesSchemaSnapshotOptionsAndOwner()
    {
        PowerPlanSnapshot snapshot = CreateSnapshot();
        EnableOptions options = new() { Ac = true, Dc = true, PreventIdleSleep = true };
        GuardState state = GuardStore.CreateState(snapshot, options, 42, CreatedUtc);
        using MemoryStream stream = new();

        GuardStore.Serialize(stream, state);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        GuardState loaded = GuardStore.Deserialize(stream);
        (PowerPlanSnapshot restored, EnableOptions restoredOptions) = GuardStore.CreateRecovery(loaded);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(snapshot.SchemeGuid, restored.SchemeGuid);
        Assert.Equal(snapshot.LidAc, restored.LidAc);
        Assert.Equal(snapshot.LidDc, restored.LidDc);
        Assert.Equal(snapshot.SleepAc, restored.SleepAc);
        Assert.Equal(snapshot.SleepDc, restored.SleepDc);
        Assert.Equal(options.Ac, restoredOptions.Ac);
        Assert.Equal(options.Dc, restoredOptions.Dc);
        Assert.Equal(options.PreventIdleSleep, restoredOptions.PreventIdleSleep);
        Assert.Equal(42, loaded.ownerProcessId);
        Assert.Equal(CreatedUtc, loaded.createdUtc);
    }

    [Theory]
    [MemberData(nameof(InvalidJson))]
    public void Deserialize_RejectsMalformedOrDangerousState(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

        Assert.ThrowsAny<SerializationException>(() => GuardStore.Deserialize(stream));
    }

    [Fact]
    public void ValidatorRejectsInvalidLidValueBeforeAnyWritePlanCanBeUsed()
    {
        GuardState state = GuardStore.CreateState(CreateSnapshot(), new EnableOptions { Ac = true }, 42, CreatedUtc);
        state.lidAc = uint.MaxValue;

        Assert.False(GuardStateValidator.TryValidate(state, out string error));
        Assert.Contains("lid action", error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SerializationException>(() => GuardStore.CreateRecovery(state));
    }

    [Fact]
    public void SaveExclusive_NeverOverwritesTheFirstRecoveryState()
    {
        string directory = Path.Combine(Path.GetTempPath(), "YingqiTools-GuardStore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "recovery.json");
        try
        {
            GuardState first = GuardStore.CreateState(CreateSnapshot(), new EnableOptions { Ac = true }, 42, CreatedUtc);
            PowerPlanSnapshot different = CreateSnapshot();
            different.LidAc = 3;
            GuardState second = GuardStore.CreateState(different, new EnableOptions { Ac = true }, 43, CreatedUtc.AddSeconds(1));

            GuardStore.SaveExclusive(path, first);
            Assert.Throws<IOException>(() => GuardStore.SaveExclusive(path, second));

            using FileStream stream = File.OpenRead(path);
            GuardState persisted = GuardStore.Deserialize(stream);
            Assert.Equal(42, persisted.ownerProcessId);
            Assert.Equal(1u, persisted.lidAc);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static TheoryData<string> InvalidJson => new()
    {
        "{}",
        ValidJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
        ValidJson().Replace(Scheme.ToString(), Guid.Empty.ToString()),
        ValidJson().Replace("\"ownerProcessId\":42", "\"ownerProcessId\":0"),
        ValidJson().Replace("\"ac\":true", "\"ac\":false"),
        ValidJson().Replace("\"lidAc\":1", "\"lidAc\":4"),
        ValidJson().TrimEnd('}') + ",\"rawPowerValue\":4294967295}"
    };

    private static string ValidJson()
    {
        GuardState state = GuardStore.CreateState(CreateSnapshot(), new EnableOptions { Ac = true }, 42, CreatedUtc);
        using MemoryStream stream = new();
        GuardStore.Serialize(stream, state);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static PowerPlanSnapshot CreateSnapshot() => new()
    {
        SchemeGuid = Scheme,
        LidAc = 1,
        LidDc = 1,
        SleepAc = 0,
        SleepDc = 180
    };
}
