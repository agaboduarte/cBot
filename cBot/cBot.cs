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
        private const string label = "trend-cBot";
        private double totalStopLossToday = 0;
        private DateTime lastTime;
        private TimeSpan fridayCloseAllOrdersUpTo = new TimeSpan(19, 0, 0);
        private double lastProfitPips = 0;
        private MovingAverage longAverage;
        private MovingAverage shortAverage;
        private RelativeStrengthIndex relativeStrengthIndex;
        private BollingerBands bollingerBands;
        private TradeVolumeIndex tradeVolumeIndex;

        [Parameter("Bollinger Periods", DefaultValue = 21, MinValue = 1, Step = 1)]
        public int BollingerPeriods { get; set; }

        [Parameter("Long Periods", DefaultValue = 21, MinValue = 1, Step = 1)]
        public int LongPeriods { get; set; }

        [Parameter("Short Periods", DefaultValue = 14, MinValue = 1, Step = 1)]
        public int ShortPeriods { get; set; }

        [Parameter("Average Distance Long/Short Pips", DefaultValue = 3.5, MinValue = 0, Step = 0.1)]
        public double AverageDistanceLongShortPips { get; set; }

        [Parameter("Difference High/Low Pips", DefaultValue = 3.5, MinValue = 0.01, Step = 0.1)]
        public double DifferenceHighLowPips { get; set; }

        [Parameter("Quantity (Lots)", DefaultValue = 0.15, MinValue = 0.01, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Stop Loss Pips", DefaultValue = 35, MinValue = 1)]
        public int StopLossPips { get; set; }

        [Parameter("Trailing Stop", DefaultValue = false)]
        public bool TrailingStopLoss { get; set; }

        [Parameter("Take Profit Pips", DefaultValue = 135, MinValue = 0)]
        public int TakeProfitPips { get; set; }

        // [Parameter("Breakeven On Profit Pips", DefaultValue = 10, MinValue = 0)]
        public int BreakevenOnProfitPips { get; set; }

        [Parameter("Max Stop Loss Per Day ($)", DefaultValue = 0, MinValue = 0, Step = 10.0)]
        public double MaxStopLossPerDay { get; set; }

        [Parameter("Buy Enabled", DefaultValue = true)]
        public bool BuyEnabled { get; set; }

        [Parameter("Sell Enabled", DefaultValue = true)]
        public bool SellEnabled { get; set; }

        private bool BuySignal
        {
            get
            {
                if (Math.Abs(MarketSeries.Open.Last(1) - MarketSeries.Close.Last(1)) / Symbol.PipSize > DifferenceHighLowPips)
                    return false;

                var averageShortPrice = shortAverage.Result.Sum(ShortPeriods) / ShortPeriods;
                var averageLongPrice = longAverage.Result.Sum(ShortPeriods) / ShortPeriods;

                if (Math.Abs(averageShortPrice - averageLongPrice) / Symbol.PipSize < AverageDistanceLongShortPips)
                    return false;

                if (MarketSeries.Close.Last(1) >= bollingerBands.Main.Last(1))
                    return false;

                if (!shortAverage.Result.IsRising())
                    return false;

                return shortAverage.Result.HasCrossedAbove(longAverage.Result, 1);
            }
        }

        private bool SellSignal
        {
            get
            {
                if (Math.Abs(MarketSeries.Open.Last(1) - MarketSeries.Close.Last(1)) / Symbol.PipSize > DifferenceHighLowPips)
                    return false;

                var averageShortPrice = shortAverage.Result.Sum(ShortPeriods) / ShortPeriods;
                var averageLongPrice = longAverage.Result.Sum(ShortPeriods) / ShortPeriods;

                if (Math.Abs(averageShortPrice - averageLongPrice) / Symbol.PipSize < AverageDistanceLongShortPips)
                    return false;

                if (MarketSeries.Close.Last(1) <= bollingerBands.Main.Last(1))
                    return false;

                if (!shortAverage.Result.IsFalling())
                    return false;

                return shortAverage.Result.HasCrossedBelow(longAverage.Result, 1);
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

            longAverage = Indicators.ExponentialMovingAverage(MarketSeries.Close, LongPeriods);
            shortAverage = Indicators.ExponentialMovingAverage(MarketSeries.Close, ShortPeriods);
            bollingerBands = Indicators.BollingerBands(MarketSeries.Close, BollingerPeriods, 2, MovingAverageType.Exponential);
            tradeVolumeIndex = Indicators.TradeVolumeIndex(MarketSeries.Close);

            base.OnStart();
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

            base.OnTick();
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

            if (BuySignal && sellPosition != null)
                ClosePosition(sellPosition);

            if (SellSignal && buyPosition != null)
                ClosePosition(buyPosition);

            if (CanOpenOrder)
            {
                if (BuySignal && BuyEnabled)
                    tradeResult = CreateOrder(TradeType.Buy, QuantityVolumeInUnits);

                if (SellSignal && SellEnabled)
                    tradeResult = CreateOrder(TradeType.Sell, QuantityVolumeInUnits);
            }

            if (tradeResult != null)
                lastProfitPips = 0;

            return tradeResult;
        }

        private TradeResult CreateOrder(TradeType type, long volume)
        {
            var position = Positions.Find(label, Symbol, type);

            if (position != null)
                return null;

            return ExecuteMarketOrder(type, Symbol, volume, label, StopLossPips, TakeProfitPips);
        }

        private void ModifyOpenPosition()
        {
            var buyPosition = Positions.Find(label, Symbol, TradeType.Buy);
            var sellPosition = Positions.Find(label, Symbol, TradeType.Sell);
            var position = buyPosition ?? sellPosition;

            if (position == null)
                return;

            if (position.Pips > lastProfitPips)
                lastProfitPips = position.Pips;

            var setStopLoss = position.StopLoss;

            if (TrailingStopLoss)
                setStopLoss = GetAbsoluteStopLoss(position, StopLossPips - lastProfitPips);

            if (position.StopLoss != setStopLoss)
                ModifyPosition(position, setStopLoss, position.TakeProfit);
        }

        private void ModifyOpenPosition2()
        {
            var buyPosition = Positions.Find(label, Symbol, TradeType.Buy);
            var sellPosition = Positions.Find(label, Symbol, TradeType.Sell);
            var position = buyPosition ?? sellPosition;

            if (position == null)
                return;

            var stopLossInProfitZone = position.TradeType == TradeType.Buy ? position.StopLoss > position.EntryPrice : position.StopLoss < position.EntryPrice;
            var setStopLoss = position.StopLoss.Value;
            var slideStop = position.Pips > lastProfitPips;
            var slidePips = slideStop ? position.Pips - lastProfitPips : 0;

            if (BreakevenOnProfitPips > 0 && position.Pips >= BreakevenOnProfitPips && !stopLossInProfitZone)
            {
                setStopLoss = position.EntryPrice;
                slidePips -= BreakevenOnProfitPips;
            }

            if (TrailingStopLoss)
            {
                lastProfitPips = position.Pips;

                setStopLoss = Math.Round(position.TradeType == TradeType.Buy ? setStopLoss + Symbol.PipSize * slidePips : setStopLoss - Symbol.PipSize * slidePips, Symbol.Digits);
            }

            if (position.StopLoss != setStopLoss)
                ModifyPosition(position, setStopLoss, position.TakeProfit);
        }

        private void OnClosePosition(PositionClosedEventArgs args)
        {
            var position = args.Position;
            var stopLoss = position.GrossProfit < 0;

            if (stopLoss)
                totalStopLossToday += Math.Abs(position.GrossProfit);
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
