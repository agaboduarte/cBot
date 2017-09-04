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
    public class cBot : Robot
    {
        private RelativeStrengthIndex rsi;
        private const string label = "trend-cBot";
        private double totalStopLossToday = 0;
        private DateTime lastTime;
        private TimeSpan fridayCloseAllOrdersUpTo = new TimeSpan(20, 0, 0);
        private int martingaleMultiplication = 1;
        private double movableStopLossLastPips = 0;
        private double movableStopLossForwardPips = 0;

        [Parameter("RSI Periods", DefaultValue = 14, MinValue = 1, Step = 1)]
        public int RSILongPeriods { get; set; }

        [Parameter("RSI Line Maximum", DefaultValue = 80, MinValue = 0, MaxValue = 100, Step = 1)]
        public int RSILineHigh { get; set; }

        [Parameter("RSI Line Minimum", DefaultValue = 20, MinValue = 0, MaxValue = 100, Step = 1)]
        public int RSILineLow { get; set; }

        [Parameter("Quantity (Lots)", DefaultValue = 0.15, MinValue = 0.01, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Stop Loss Pips", DefaultValue = 35, MinValue = 1, Step = 0.01)]
        public double StopLossPips { get; set; }

        [Parameter("Movable Stop Loss", DefaultValue = false)]
        public bool MovableStopLoss { get; set; }

        [Parameter("Take Profit Pips", DefaultValue = 135, MinValue = 0, Step = 0.01)]
        public double TakeProfitPips { get; set; }

        [Parameter("Max Stop Loss Per Day ($)", DefaultValue = 200, MinValue = 0, Step = 10.0)]
        public double MaxStopLossPerDay { get; set; }

        [Parameter("Martingale", DefaultValue = false)]
        public bool Martingale { get; set; }

        private bool RisingSignal
        {
            get { return rsi.Result.Minimum(RSILongPeriods) <= RSILineLow && rsi.Result.LastValue <= RSILineLow && rsi.Result.IsRising(); }
        }

        private bool FallingSignal
        {
            get { return rsi.Result.Maximum(RSILongPeriods) >= RSILineHigh && rsi.Result.LastValue >= RSILineHigh && rsi.Result.IsFalling(); }
        }

        private long QuantityVolumeInUnits
        {
            get { return Symbol.QuantityToVolume(Lots); }
        }

        private bool RiskTime
        {
            get { return Time.DayOfWeek == DayOfWeek.Friday && Time.TimeOfDay >= fridayCloseAllOrdersUpTo; }
        }

        private bool CanOpenOrder
        {
            get { return !RiskTime && lastTime.Date == Time.Date && (MaxStopLossPerDay == 0 || totalStopLossToday < MaxStopLossPerDay); }
        }

        protected override void OnStart()
        {
            Positions.Closed += OnClosePosition;

            rsi = Indicators.RelativeStrengthIndex(MarketSeries.Close, RSILongPeriods);
        }

        protected override void OnTick()
        {
            if (lastTime.Date != Time.Date)
            {
                lastTime = Time;
                totalStopLossToday = 0;
            }

            ClosingOrdersIfNecessary();

            var tradeResult = CreateOrders();

            ModifyOpenPosition();

            if (tradeResult != null && tradeResult.IsSuccessful)
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
            var volume = QuantityVolumeInUnits * martingaleMultiplication;

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
            if (MovableStopLoss)
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

            if (position.Pips > movableStopLossLastPips)
                movableStopLossLastPips = position.Pips;

            var setStopLoss = GetAbsoluteStopLoss(position, StopLossPips);

            if (MovableStopLoss)
                setStopLoss = GetAbsoluteStopLoss(position, StopLossPips - movableStopLossLastPips);

            if (position.StopLoss != setStopLoss)
                ModifyPosition(position, setStopLoss, position.TakeProfit);
        }

        private void EndConfigureParameters()
        {
            movableStopLossLastPips = 0;
        }

        private void OnClosePosition(PositionClosedEventArgs args)
        {
            var position = args.Position;
            var stopLoss = position.GrossProfit < 0;

            if (stopLoss)
            {
                totalStopLossToday += Math.Abs(position.GrossProfit);

                if (Martingale)
                    martingaleMultiplication++;
            }
            else
                martingaleMultiplication = 1;
        }

        private double GetAbsoluteStopLoss(Position position, double stopLossInPips)
        {
            return position.TradeType == TradeType.Buy ? position.EntryPrice - Symbol.PipSize * stopLossInPips : position.EntryPrice + Symbol.PipSize * stopLossInPips;
        }
    }
}
