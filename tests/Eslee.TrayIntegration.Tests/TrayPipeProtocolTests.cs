using Eslee.TrayIntegration;

namespace Eslee.TrayIntegration.Tests;

[TestClass]
public sealed class TrayPipeProtocolTests
{
    [TestMethod]
    public void BuildPipeNameSanitizesUnsupportedCharacters()
    {
        // AutoPower 저장소의 TrayHostLinkTests와 같은 기대값을 사용해 규약 일치를 확인합니다.
        Assert.AreEqual(
            "eslee.trayfolder.tray-host.v1.user-1_a--",
            TrayPipeProtocol.BuildPipeName("user 1_a!한"));
    }

    [TestMethod]
    public void SerializeOmitsNullFields()
    {
        var json = TrayPipeProtocol.Serialize(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.SetTrayModeType,
            Mode = TrayPipeProtocol.HostedModeValue,
        });

        StringAssert.Contains(json, "\"type\":\"set-tray-mode\"");
        StringAssert.Contains(json, "\"mode\":\"hosted\"");
        Assert.IsFalse(json.Contains("appId", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("errorMessage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryDeserializeRoundTripsAndIgnoresMalformedLines()
    {
        var original = new TrayPipeMessage
        {
            Type = TrayPipeProtocol.CommandType,
            Id = 12,
            Command = TrayPipeProtocol.ActivateCommandValue,
        };

        var parsed = TrayPipeProtocol.TryDeserialize(TrayPipeProtocol.Serialize(original));

        Assert.IsNotNull(parsed);
        Assert.AreEqual(original.Type, parsed.Type);
        Assert.AreEqual(original.Id, parsed.Id);
        Assert.AreEqual(original.Command, parsed.Command);
        Assert.IsNull(TrayPipeProtocol.TryDeserialize("{not json"));
        Assert.IsNull(TrayPipeProtocol.TryDeserialize("   "));
    }

    [TestMethod]
    public void TryCreateRegistrationAcceptsValidVersion1Message()
    {
        var registration = TrayPipeProtocol.TryCreateRegistration(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.RegisterType,
            ProtocolVersion = 1,
            AppId = TrayPipeProtocol.AutoPowerAppId,
            DisplayName = "eslee Auto Power",
            ProcessId = 4321,
            Mode = TrayPipeProtocol.StandaloneModeValue,
        });

        Assert.IsNotNull(registration);
        Assert.AreEqual(TrayPipeProtocol.AutoPowerAppId, registration.AppId);
        Assert.AreEqual("eslee Auto Power", registration.DisplayName);
        Assert.AreEqual(TrayMode.Standalone, registration.RequestedMode);
        Assert.AreEqual(4321, registration.ProcessId);
    }

    [TestMethod]
    public void TryCreateRegistrationRejectsInvalidMessages()
    {
        Assert.IsNull(TrayPipeProtocol.TryCreateRegistration(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.RegisterType,
            ProtocolVersion = 2,
            AppId = TrayPipeProtocol.AutoPowerAppId,
            ProcessId = 1,
        }));
        Assert.IsNull(TrayPipeProtocol.TryCreateRegistration(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.RegisterType,
            ProtocolVersion = 1,
            AppId = " ",
            ProcessId = 1,
        }));
        Assert.IsNull(TrayPipeProtocol.TryCreateRegistration(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.RegisterType,
            ProtocolVersion = 1,
            AppId = TrayPipeProtocol.AutoPowerAppId,
        }));
        Assert.IsNull(TrayPipeProtocol.TryCreateRegistration(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.CommandType,
            ProtocolVersion = 1,
            AppId = TrayPipeProtocol.AutoPowerAppId,
            ProcessId = 1,
        }));
    }

    [TestMethod]
    public void MenuItemsRoundTripThroughWirePayloads()
    {
        var items = new List<TrayMenuItem>
        {
            TrayMenuItem.Action("open-app", "앱 열기"),
            TrayMenuItem.Separator,
            TrayMenuItem.Action("next-schedule", "다음 예약: 내일 오전 7:00", enabled: false),
        };

        var json = TrayPipeProtocol.Serialize(new TrayPipeMessage
        {
            Type = TrayPipeProtocol.MenuType,
            Id = 3,
            Items = TrayPipeProtocol.ToMenuPayloads(items),
        });

        // enabled=true는 생략되고, 구분선은 separator만 갖습니다.
        StringAssert.Contains(json, "\"separator\":true");
        Assert.IsFalse(json.Contains("\"enabled\":true", StringComparison.Ordinal));
        StringAssert.Contains(json, "\"enabled\":false");

        var parsed = TrayPipeProtocol.TryDeserialize(json);
        Assert.IsNotNull(parsed);
        var roundTripped = TrayPipeProtocol.ToMenuItems(parsed);
        Assert.HasCount(3, roundTripped);
        Assert.AreEqual(items[0], roundTripped[0]);
        Assert.IsTrue(roundTripped[1].IsSeparator);
        Assert.AreEqual(items[2], roundTripped[2]);
    }

    [TestMethod]
    public void ToMenuItemsSkipsInvalidEntriesAndAppliesDefaults()
    {
        var parsed = TrayPipeProtocol.TryDeserialize(
            """{"type":"menu","id":1,"items":[{"id":"a","text":"항목"},{"text":"  "},{"id":"only-id"},{"separator":true},null]}""");

        Assert.IsNotNull(parsed);
        var items = TrayPipeProtocol.ToMenuItems(parsed);

        Assert.HasCount(2, items);
        Assert.AreEqual(new TrayMenuItem("a", "항목", true, false), items[0]);
        Assert.IsTrue(items[1].IsSeparator);
    }

    [TestMethod]
    public void ToMenuItemsReturnsEmptyWhenItemsMissing()
    {
        var parsed = TrayPipeProtocol.TryDeserialize("""{"type":"menu","id":1}""");

        Assert.IsNotNull(parsed);
        Assert.IsEmpty(TrayPipeProtocol.ToMenuItems(parsed));
    }

    [TestMethod]
    public void TryParseTrayModeParsesKnownValuesCaseInsensitively()
    {
        Assert.IsTrue(TrayPipeProtocol.TryParseTrayMode("Hosted", out var hosted));
        Assert.AreEqual(TrayMode.Hosted, hosted);
        Assert.IsTrue(TrayPipeProtocol.TryParseTrayMode("standalone", out var standalone));
        Assert.AreEqual(TrayMode.Standalone, standalone);
        Assert.IsFalse(TrayPipeProtocol.TryParseTrayMode("attached", out _));
        Assert.IsFalse(TrayPipeProtocol.TryParseTrayMode(null, out _));
    }
}
