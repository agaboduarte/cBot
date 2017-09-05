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
        private SimpleMovingAverage simpleMovingAverage;
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

        [Parameter("Crossing Periods", DefaultValue = 3, MinValue = 1)]
        public int CrossingPeriods { get; set; }

        [Parameter("High Ceil", DefaultValue = 70, MinValue = 1, MaxValue = 100, Step = 1)]
        public int HighCeil { get; set; }

        [Parameter("Low Ceil", DefaultValue = 30, MinValue = 1, MaxValue = 100, Step = 1)]
        public int LowCeil { get; set; }

        [Parameter("Quantity (Lots)", DefaultValue = 0.15, MinValue = 0.01, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Stop Loss Pips", DefaultValue = 35, MinValue = 1)]
        public int StopLossPips { get; set; }

        [Parameter("Trailing Stop", DefaultValue = false)]
        public bool TrailingStopLoss { get; set; }

        [Parameter("Take Profit Pips", DefaultValue = 135, MinValue = 0)]
        public int TakeProfitPips { get; set; }

        [Parameter("Max Stop Loss Per Day ($)", DefaultValue = 0, MinValue = 0, Step = 10.0)]
        public double MaxStopLossPerDay { get; set; }

        [Parameter("Buy Enabled", DefaultValue = true)]
        public bool BuyEnabled { get; set; }

        [Parameter("Sell Enabled", DefaultValue = true)]
        public bool SellEnabled { get; set; }

        [Parameter("Martingale", DefaultValue = false)]
        public bool Martingale { get; set; }

        private bool BuySignal
        {
            get
            {
                var sma = simpleMovingAverage;
                var belowCeiling = false;

                for (var i = 0; i < CrossingPeriods; i++)
                {
                    if (relativeStrengthIndex.Result.Last(i) < LowCeil)
                    {
                        belowCeiling = true;
                        break;
                    }
                }

                if (!belowCeiling)
                    return false;

                for (var i = 0; i < CrossingPeriods; i++)
                {
                    if (sma.Result.Last(i) < MarketSeries.Close.Last(i))
                        return true;
                }

                return false;
            }
        }

        private bool SellSignal
        {
            get
            {
                var sma = simpleMovingAverage;
                var aboveCeiling = false;

                for (var i = 0; i < CrossingPeriods; i++)
                {
                    if (relativeStrengthIndex.Result.Last(i) > HighCeil)
                    {
                        aboveCeiling = true;
                        break;
                    }
                }

                if (!aboveCeiling)
                    return false;

                for (var i = 0; i < CrossingPeriods; i++)
                {
                    if (sma.Result.Last(i) > MarketSeries.Close.Last(i))
                        return true;
                }

                return false;
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
            get { return !RiskTime && lastTime.Date == Time.Date && (MaxStopLossPerDay == 0 || totalStopLossToday < MaxStopLossPerDay) && Positions.FindAll(label, Symbol).Length == 0; }
        }

        protected override void OnStart()
        {
            Positions.Closed += OnClosePosition;

            simpleMovingAverage = Indicators.SimpleMovingAverage(MarketSeries.Close, Periods);
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

            if (CanOpenOrder)
            {
                if (BuySignal && buyPosition == null)
                {
                    CloseAllPositions();

                    if (BuyEnabled)
                        tradeResult = CreateOrder(TradeType.Buy, volume);
                }

                if (SellSignal && sellPosition == null)
                {
                    CloseAllPositions();

                    if (SellEnabled)
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
