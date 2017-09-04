// -------------------------------------------------------------------------------------------------
//
//    This code is a cAlgo API sample.
//
//    This cBot is intended to be used as a sample and does not guarantee any particular outcome or
//    profit of any kind. Use it at your own risk.
//
//    The "Sample Trend cBot" will buy when fast period moving average crosses the slow period moving average and sell when 
//    the fast period moving average crosses the slow period moving average. The orders are closed when an opposite signal 
//    is generated. There can only by one Buy or Sell order at any time.
//
// -------------------------------------------------------------------------------------------------

using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MyBot : Robot
    {
        private RelativeStrengthIndex rsi;
        private const string label = "Sample Trend cBot";
        private object lockCreateOrder = new object();
        private double totalStopLossToday = 0;
        private DateTime lastTime;
        private TimeSpan fridayCloseAllOrdersUpTo = new TimeSpan(20, 0, 0);
        private double stopLossMovableLastPips = 0;
        private int martingaleVariant = 1;

        [Parameter("DataSeries")]
        public DataSeries Series { get; set; }

        [Parameter("RSI Periods", DefaultValue = 14, MinValue = 1)]
        public int RSIPeriods { get; set; }

        [Parameter("RSI Line Maximum", DefaultValue = 80, MinValue = 0, MaxValue = 100)]
        public int RSILineMaximum { get; set; }

        [Parameter("RSI Line Minimum", DefaultValue = 20, MinValue = 0, MaxValue = 100)]
        public int RSILineMinimum { get; set; }

        [Parameter("Quantity (Lots)", DefaultValue = 0.15, MinValue = 0.01, Step = 0.01)]
        public double Quantity { get; set; }

        [Parameter("Use Small Quantity On Stop Loss", DefaultValue = false)]
        public bool UseSmallQuantityOnStopLoss { get; set; }

        [Parameter("Stop Loss Pips", DefaultValue = 45, MinValue = 1)]
        public double StopLossPips { get; set; }

        [Parameter("Stop Loss Movable Pips", DefaultValue = 0, MinValue = 0)]
        public double StopLossMovablePips { get; set; }

        [Parameter("Take Profit Pips", DefaultValue = 135, MinValue = 0)]
        public double TakeProfitPips { get; set; }

        [Parameter("Breakeven Strategy", DefaultValue = true)]
        public bool BreakevenStrategy { get; set; }

        [Parameter("Breakeven Pips", DefaultValue = 2, MinValue = 0)]
        public double BreakevenPips { get; set; }

        [Parameter("Max Stop Loss Per Day ($)", DefaultValue = 200, MinValue = 0)]
        public double MaxStopLossPerDay { get; set; }

        [Parameter("Use Martingale", DefaultValue = false)]
        public bool UseMartingale { get; set; }

        private bool RisingSignal
        {
            get { return rsi.Result.Minimum(RSIPeriods) <= RSILineMinimum && rsi.Result.LastValue <= RSILineMinimum && rsi.Result.IsRising(); }
        }

        private bool FallingSignal
        {
            get { return rsi.Result.Maximum(RSIPeriods) >= RSILineMaximum && rsi.Result.LastValue >= RSILineMaximum && rsi.Result.IsFalling(); }
        }

        private long QuantityVolumeInUnits
        {
            get { return Symbol.QuantityToVolume(Quantity); }
        }

        private bool RiskTime
        {
            get { return Time.DayOfWeek == DayOfWeek.Friday && Time.TimeOfDay >= fridayCloseAllOrdersUpTo; }
        }

        private bool CanOpenOrder
        {
            get { return !RiskTime && lastTime.Date == Time.Date && (MaxStopLossPerDay == 0 || totalStopLossToday < MaxStopLossPerDay); }
        }

        private bool IsMovableStopLoss
        {
            get { return StopLossMovablePips > 0; }
        }

        protected override void OnStart()
        {
            Positions.Closed += OnClosePosition;

            rsi = Indicators.RelativeStrengthIndex(Series, RSIPeriods);
        }

        protected override void OnTick()
        {
            if (lastTime.Date != Time.Date)
            {
                lastTime = Time;
                totalStopLossToday = 0;
            }

            ClosingOrdersIfNecessary();

            var trade = CreateOrders();

            ModifyOpenPosition();

            if (trade != null && trade.IsSuccessful)
                EndConfigureParameters();
        }

        private void ClosingOrdersIfNecessary()
        {
            if (RiskTime)
            {
                foreach (var position in Positions.FindAll(label, Symbol))
                    ClosePosition(position);
            }
        }

        private TradeResult CreateOrders()
        {
            var tradeResult = default(TradeResult);
            var buyPosition = Positions.Find(label, Symbol, TradeType.Buy);
            var sellPosition = Positions.Find(label, Symbol, TradeType.Sell);
            var volume = QuantityVolumeInUnits * martingaleVariant;

            if (CanOpenOrder && tradeResult == null)
            {
                if (RisingSignal && buyPosition == null)
                {
                    foreach (var position in Positions.FindAll(label, Symbol, TradeType.Sell))
                        ClosePosition(position);

                    tradeResult = CreateOrder(TradeType.Buy, volume);
                }

                if (FallingSignal && sellPosition == null)
                {
                    foreach (var position in Positions.FindAll(label, Symbol, TradeType.Buy))
                        ClosePosition(position);

                    tradeResult = CreateOrder(TradeType.Sell, volume);
                }
            }

            return tradeResult;
        }

        private TradeResult CreateOrder(TradeType type, long volume)
        {
            if (IsMovableStopLoss)
                return ExecuteMarketOrder(type, Symbol, volume, label, StopLossPips, null);

            return ExecuteMarketOrder(type, Symbol, volume, label, StopLossPips, TakeProfitPips);
        }

        private void ModifyOpenPosition()
        {
            var buyPosition = Positions.Find(label, Symbol, TradeType.Buy);
            var sellPosition = Positions.Find(label, Symbol, TradeType.Sell);
            var position = buyPosition ?? sellPosition;

            if (position == null)
                return;

            var breakevenValue = BreakevenPips * Symbol.PipValue;
            var stopLossValue = StopLossMovablePips * Symbol.PipValue;
            var takeProfitValue = TakeProfitPips * Symbol.PipValue;

            var isBreakevenPoint = position.Pips >= StopLossPips;
            var takeProfit = default(double?);
            var stopLoss = position.StopLoss.Value;

            if (IsMovableStopLoss)
                stopLoss = position.TradeType == TradeType.Sell ? position.StopLoss.Value - stopLossValue : position.StopLoss.Value + stopLossValue;
            else
            {
                if (isBreakevenPoint)
                    stopLoss = position.TradeType == TradeType.Sell ? position.EntryPrice - breakevenValue : position.EntryPrice + breakevenValue;

                takeProfit = position.TradeType == TradeType.Sell ? position.EntryPrice - takeProfitValue : position.EntryPrice + takeProfitValue;
            }

            var incrementMovableStopLoss = IsMovableStopLoss && position.Pips - stopLossMovableLastPips >= StopLossMovablePips;

            if (BreakevenStrategy && isBreakevenPoint || incrementMovableStopLoss)
                ModifyPosition(position, stopLoss, takeProfit);

            if (position.Pips > stopLossMovableLastPips)
                stopLossMovableLastPips = position.Pips;
        }

        private void EndConfigureParameters()
        {
            stopLossMovableLastPips = 0;
        }

        private void OnClosePosition(PositionClosedEventArgs args)
        {
            var position = args.Position;
            var stopLoss = position.GrossProfit < 0;

            if (stopLoss)
            {
                totalStopLossToday += Math.Abs(position.GrossProfit);

                if (UseMartingale)
                    martingaleVariant++;
            }
            else
                martingaleVariant = 1;
        }
    }
}
