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
        private RelativeStrengthIndex relativeStrengthIndex;
        private const string label = "trend-cBot";
        private double totalStopLossToday = 0;
        private DateTime lastTime;
        private TimeSpan fridayCloseAllOrdersUpTo = new TimeSpan(20, 0, 0);
        private int martingaleMultiplication = 1;
        private double movableStopLossLastPips = 0;
        private double movableStopLossForwardPips = 0;

        [Parameter("Periods", DefaultValue = 14, MinValue = 1, Step = 1)]
        public int Periods { get; set; }

        [Parameter("High Ceil", DefaultValue = 80, MinValue = 0, MaxValue = 100, Step = 1)]
        public int HighCeil { get; set; }

        [Parameter("Low Ceil", DefaultValue = 20, MinValue = 0, MaxValue = 100, Step = 1)]
        public int LowCeil { get; set; }

        [Parameter("Quantity (Lots)", DefaultValue = 0.15, MinValue = 0.01, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Stop Loss Pips", DefaultValue = 35, MinValue = 1)]
        public int StopLossPips { get; set; }

        [Parameter("Trailing Stop", DefaultValue = false)]
        public bool TrailingStopLoss { get; set; }

        [Parameter("Take Profit Pips", DefaultValue = 135, MinValue = 0, Step = 0.01)]
        public double TakeProfitPips { get; set; }

        [Parameter("Max Stop Loss Per Day ($)", DefaultValue = 200, MinValue = 0, Step = 10.0)]
        public double MaxStopLossPerDay { get; set; }

        [Parameter("Martingale", DefaultValue = false)]
        public bool Martingale { get; set; }

        private bool BuySignal
        {
            get
            {
                var rsi = relativeStrengthIndex;

                return rsi.Result.Last(1) < LowCeil && rsi.Result.IsRising() && rsi.Result.Sum(Periods) / Periods > LowCeil;
            }
        }

        private bool SellSignal
        {
            get
            {
                var rsi = relativeStrengthIndex;

                return rsi.Result.Last(1) > HighCeil && rsi.Result.IsFalling() && rsi.Result.Sum(Periods) / Periods < HighCeil;
            }
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

            relativeStrengthIndex = Indicators.RelativeStrengthIndex(MarketSeries.Close, Periods);
        }

        protected override void OnTick()
        {
            if (lastTime.Date != Time.Date)
            {
                lastTime = Time;
                totalStopLossToday = 0;
            }

            ClosingOrdersIfNecessary();
            CreateOrders();
            ModifyOpenPosition();
        }

        private void ClosingOrdersIfNecessary()
        {
            if (RiskTime)
                CloseAllPositions();
        }

        private TradeResult CreateOrders()
        {
            var tradeResult = default(TradeResult);
            var buyPosition = Positions.Find(label, Symbol, TradeType.Buy);
            var sellPosition = Positions.Find(label, Symbol, TradeType.Sell);
            var volume = QuantityVolumeInUnits * martingaleMultiplication;

            if (CanOpenOrder && tradeResult == null)
            {
                if (BuySignal && buyPosition == null)
                {
                    CloseAllPositions();

                    tradeResult = CreateOrder(TradeType.Buy, volume);
                }

                if (SellSignal && sellPosition == null)
                {
                    CloseAllPositions();

                    tradeResult = CreateOrder(TradeType.Sell, volume);
                }
            }

            if (tradeResult != null)
                movableStopLossLastPips = 0;

            return tradeResult;
        }

        private TradeResult CreateOrder(TradeType type, long volume)
        {
            var position = Positions.Find(label, Symbol, type);

            if (position != null)
                return null;

            if (TrailingStopLoss)
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

            if (TrailingStopLoss)
                setStopLoss = GetAbsoluteStopLoss(position, StopLossPips - movableStopLossLastPips);

            if (position.StopLoss != setStopLoss)
                ModifyPosition(position, setStopLoss, position.TakeProfit);
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

        private void CloseAllPositions()
        {
            foreach (var position in Positions.FindAll(label, Symbol))
                ClosePosition(position);
        }
    }
}
