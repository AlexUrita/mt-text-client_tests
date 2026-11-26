using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Commands;
using MTTextClient.Core;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Pins the MTCore 0.7.26721 wire layout of the structs the client builds itself.
/// 26721 inserted <c>(byte)maxOrderBufferType</c> + <c>(float)maxOrderBuffer</c> into
/// <see cref="OrderRequestData"/> right after <c>orderAutoAdjustment</c>; a client still
/// linked against the 26548 DLL omitted those 5 bytes, so the core mis-parsed every
/// place-order request and never answered ("sent (response timed out)").
/// </summary>
public sealed class MtCore26721WireLayoutUnitTests
{
    private static OrderRequestData SampleOrder()
    {
        return new OrderRequestData
        {
            exchangeType = ExchangeType.BINANCE,
            marketType = MarketType.FUTURES,
            orderSideType = OrderSideType.BUY,
            positionSide = PositionSide.BOTH,
            symbol = "BTCUSDT",
            clientOrderId = "cid-1",
            parentClientOrderId = "",
            qty = 0.002m,
            price = 50000m,
            stopPrice = 0m,
            useReduceOnly = true,
            orderAutoAdjustment = OrderAutoAdjustmentType.MIN,
            maxOrderBufferType = OrderAutoAdjustmentBufferType.PERCENTAGE,
            maxOrderBuffer = 12.5f,
            rejectOnPossibleLiquidaiton = false,
            groupID = 7,
            stopLossSettings = new StopLossSettings
            {
                isOn = true,
                percentage = 3f,
                dontMoveLimitStopAfterPrice = true,
            },
        };
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Vendor_pin_is_core_26721()
    {
        CoreStatusStore.ExpectedCoreBuild.Should().Be("0.7.26721");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderRequestData_writes_buffer_fields_between_autoAdjustment_and_rejectOnLiquidation()
    {
        byte[] bytes = SampleOrder().Serialize();
        bytes.Should().NotBeNull();

        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.ReadByte().Should().Be((byte)ExchangeType.BINANCE);
        reader.ReadByte().Should().Be((byte)MarketType.FUTURES);
        reader.ReadInt16().Should().Be((Int16)OrderSideType.BUY);
        reader.ReadByte().Should().Be((byte)PositionSide.BOTH);
        reader.ReadString().Should().Be("BTCUSDT");
        reader.ReadString().Should().Be("cid-1");
        reader.ReadString().Should().Be("");
        reader.ReadDecimal().Should().Be(0.002m);
        reader.ReadDecimal().Should().Be(50000m);
        reader.ReadDecimal().Should().Be(0m);
        reader.ReadBoolean().Should().BeFalse("isStopLoss");
        reader.ReadBoolean().Should().BeFalse("isTakeProfit");
        reader.ReadByte();                                  // tpslStatus
        reader.ReadBoolean().Should().BeTrue("useReduceOnly");
        reader.ReadByte().Should().Be((byte)OrderAutoAdjustmentType.MIN);
        // 0.7.26721 additions:
        reader.ReadByte().Should().Be((byte)OrderAutoAdjustmentBufferType.PERCENTAGE);
        reader.ReadSingle().Should().Be(12.5f);
        // Unchanged tail continues right after them.
        reader.ReadBoolean().Should().BeFalse("rejectOnPossibleLiquidaiton");
        reader.ReadInt32().Should().Be(7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderRequestData_round_trips_new_fields()
    {
        var copy = new OrderRequestData();
        copy.Deserialize(SampleOrder().Serialize()).Should().BeTrue();

        copy.maxOrderBufferType.Should().Be(OrderAutoAdjustmentBufferType.PERCENTAGE);
        copy.maxOrderBuffer.Should().Be(12.5f);
        copy.rejectOnPossibleLiquidaiton.Should().BeFalse();
        copy.groupID.Should().Be(7);
        copy.symbol.Should().Be("BTCUSDT");
        copy.stopLossSettings.isOn.Should().BeTrue();
        copy.stopLossSettings.dontMoveLimitStopAfterPrice.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderRequestData_defaults_match_native_manual_order()
    {
        // The native client (BotClient CommandAddOrder / MoonTrader OrderFormVE) never
        // sets the buffer fields on a manual order, so it sends (byte)0 / 0f. The text
        // client leaves them at their defaults to send the same bytes.
        var order = new OrderRequestData();
        ((byte)order.maxOrderBufferType).Should().Be(0);
        order.maxOrderBuffer.Should().Be(0f);
        order.rejectOnPossibleLiquidaiton.Should().BeTrue();
        new StopLossSettings().dontMoveLimitStopAfterPrice.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AutoStopOnBalanceData_round_trips_26721_schema()
    {
        var autostop = new AutoStopOnBalanceData
        {
            id = 42,
            name = "guard",
            isRunning = true,
            marketType = MarketType.FUTURES,
            quoteAsset = "usdt",
            maxLossThreshold = AutoStopsCommand.LossThreshold(5.0),
            stopEnabled = true,
            stopKeywords = "btc",
            excludeStopKeywords = true,
            panicSellEnabled = true,
        };

        var copy = new AutoStopOnBalanceData();
        copy.Deserialize(autostop.Serialize()).Should().BeTrue();

        copy.id.Should().Be(42);
        copy.name.Should().Be("guard");
        copy.isRunning.Should().BeTrue();
        copy.quoteAsset.Should().Be("usdt");
        copy.maxLossThreshold.toggle.Should().BeTrue();
        copy.maxLossThreshold.value.Should().Be(-5.0, "native BotClient stores the loss as -|x|");
        copy.stopEnabled.Should().BeTrue();
        copy.stopKeywords.Should().Be("btc");
        copy.excludeStopKeywords.Should().BeTrue();
        copy.panicSellEnabled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AutoStopStore_keeps_pushed_baselines_across_snapshots()
    {
        var store = new AutoStopStore();
        store.ProcessEvent(new AutoStopOnBalanceValuesEvent
        {
            Values = new List<AutoStopBalanceDynamicValueData>
            {
                new() { id = 9, lossBaseLine = 1000.5, profitBaseLine = 1100.0 },
            },
        });
        store.ProcessEvent(new AutoStopListEvent
        {
            AutoStopsOnBalance = new List<AutoStopOnBalanceData> { new() { id = 9, name = "g" } },
            AutoStopsOnReports = new List<AutoStopOnReportsData>(),
        });

        store.FindBalanceValues(9)!.lossBaseLine.Should().Be(1000.5);
        store.FindBalanceValues(9)!.profitBaseLine.Should().Be(1100.0);
        store.FindBalanceValues(10).Should().BeNull();
    }
}
