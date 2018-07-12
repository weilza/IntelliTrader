﻿using ExchangeSharp;
using IntelliTrader.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace IntelliTrader.Exchange.Base
{
    public abstract class ExchangeService : ConfigrableServiceBase<ExchangeConfig>, IExchangeService
    {
        public const int SOCKET_DISPOSE_TIMEOUT_MILLISECONDS = 10000;
        public const int MAX_TICKERS_AGE_TO_RECONNECT_MILLISECONDS = 60000;
        public const int INITIAL_TICKERS_TIMEOUT_MILLISECONDS = 5000;
        public const int INITIAL_TICKERS_RETRY_LIMIT = 4;

        public override string ServiceName => Constants.ServiceNames.ExchangeService;

        protected readonly ILoggingService loggingService;
        protected readonly IHealthCheckService healthCheckService;
        protected readonly ITasksService tasksService;

        public ExchangeAPI Api { get; private set; }
        public ConcurrentDictionary<string, Ticker> Tickers { get; private set; }

        private IDisposable socket;
        private ConcurrentBag<string> markets;
        private TickersMonitorTimedTask tickersMonitorTimedTask;
        private DateTimeOffset lastTickersUpdate;
        private bool tickersStarted;

        public ExchangeService(ILoggingService loggingService, IHealthCheckService healthCheckService, ITasksService tasksService)
        {
            this.loggingService = loggingService;
            this.healthCheckService = healthCheckService;
            this.tasksService = tasksService;
        }

        public virtual void Start(bool virtualTrading)
        {
            loggingService.Info("Start Exchange service...");
            Api = InitializeApi();

            if (!virtualTrading && !String.IsNullOrWhiteSpace(Config.KeysPath))
            {
                if (File.Exists(Config.KeysPath))
                {
                    loggingService.Info("Load keys from encrypted file...");
                    Api.LoadAPIKeys(Config.KeysPath);
                }
                else
                {
                    throw new FileNotFoundException("Keys file not found");
                }
            }

            loggingService.Info("Get initial ticker values...");
            IEnumerable<KeyValuePair<string, ExchangeTicker>> exchangeTickers = null;
            for (int retry = 0; retry < INITIAL_TICKERS_RETRY_LIMIT; retry++)
            {
                Task.Run(() => exchangeTickers = Api.GetTickers()).Wait(TimeSpan.FromMilliseconds(INITIAL_TICKERS_TIMEOUT_MILLISECONDS));
                if (exchangeTickers != null) break;
            }
            if (exchangeTickers != null)
            {
                Tickers = new ConcurrentDictionary<string, Ticker>(exchangeTickers.Select(t => new KeyValuePair<string, Ticker>(t.Key, new Ticker
                {
                    Pair = t.Key,
                    AskPrice = t.Value.Ask,
                    BidPrice = t.Value.Bid,
                    LastPrice = t.Value.Last
                })));
                markets = new ConcurrentBag<string>(Tickers.Keys.Select(pair => GetPairMarket(pair)).Distinct().ToList());

                lastTickersUpdate = DateTimeOffset.Now;
                healthCheckService.UpdateHealthCheck(Constants.HealthChecks.TickersUpdated, $"Updates: {Tickers.Count}");
            }
            else if (Tickers != null)
            {
                loggingService.Error("Unable to get initial ticker values");
            }
            else
            {
                throw new Exception("Unable to get initial ticker values");
            }

            ConnectTickersWebsocket();

            loggingService.Info("Exchange service started");
        }

        public virtual void Stop()
        {
            loggingService.Info("Stop Exchange service...");

            DisconnectTickersWebsocket();
            lastTickersUpdate = DateTimeOffset.MinValue;
            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.TickersUpdated);

            loggingService.Info("Exchange service stopped");
        }

        protected abstract ExchangeAPI InitializeApi();

        public abstract IOrderDetails PlaceOrder(IOrder order, string priceCurrency = null);

        public void ConnectTickersWebsocket()
        {
            try
            {
                loggingService.Info("Connect to Exchange tickers...");
                socket = Api.GetTickersWebSocket(OnTickersUpdated);
                loggingService.Info("Connected to Exchange tickers");

                tickersMonitorTimedTask = tasksService.AddTask(
                    name: nameof(TickersMonitorTimedTask),
                    task: new TickersMonitorTimedTask(loggingService, this),
                    interval: MAX_TICKERS_AGE_TO_RECONNECT_MILLISECONDS / 2,
                    startDelay: Constants.TaskDelays.ZeroDelay,
                    startTask: tickersStarted,
                    runNow: false,
                    skipIteration: 0);
            }
            catch (Exception ex)
            {
                loggingService.Error("Unable to connect to Exchange tickers", ex);
            }
        }

        public void DisconnectTickersWebsocket()
        {
            try
            {
                tasksService.RemoveTask(nameof(TickersMonitorTimedTask), stopTask: true);

                loggingService.Info("Disconnect from Exchange tickers...");
                // Give Dispose 10 seconds to complete and then time out if not
                Task.Run(() => socket.Dispose()).Wait(TimeSpan.FromMilliseconds(SOCKET_DISPOSE_TIMEOUT_MILLISECONDS));
                socket = null;
                loggingService.Info("Disconnected from Exchange tickers");
            }
            catch (Exception ex)
            {
                loggingService.Error("Unable to disconnect from Exchange tickers", ex);
            }
        }

        public virtual IEnumerable<ITicker> GetTickers()
        {
            return Tickers.Values;
        }

        public virtual IEnumerable<string> GetMarkets()
        {
            return markets.OrderBy(m => m).AsEnumerable();
        }

        public virtual IEnumerable<string> GetMarketPairs(string market)
        {
            return Tickers.Keys.Where(t => t.EndsWith(market));
        }

        public virtual Dictionary<string, decimal> GetAvailableAmounts()
        {
            return Api.GetAmountsAvailableToTradeAsync().Result;
        }

        public abstract IEnumerable<IOrderDetails> GetTrades(string pair);

        public virtual decimal GetPrice(string pair, TradePriceType priceType)
        {
            if (Tickers.TryGetValue(pair, out Ticker ticker))
            {
                if (priceType == TradePriceType.Ask)
                {
                    return ticker.AskPrice;
                }
                else if (priceType == TradePriceType.Bid)
                {
                    return ticker.BidPrice;
                }
                else
                {
                    return ticker.LastPrice;
                }
            }
            else
            {
                return 0;
            }
        }

        public virtual decimal GetPriceSpread(string pair)
        {
            if (Tickers.TryGetValue(pair, out Ticker ticker))
            {
                return Utils.CalculatePercentage(ticker.BidPrice, ticker.AskPrice);
            }
            else
            {
                return 0;
            }
        }

        public abstract decimal GetPriceArbitrage(string pair, string crossMarket, string market);

        public abstract string GetArbitrageMarketPair(string crossMarket);

        public virtual string GetPairMarket(string pair)
        {
            return Api.ExchangeSymbolToGlobalSymbol(pair).Split('-')[0];
        }

        public virtual string ChangeMarket(string pair, string market)
        {
            if (!pair.EndsWith(market))
            {
                string currentMarket = GetPairMarket(pair);
                return pair.Substring(0, pair.Length - currentMarket.Length) + market;
            }
            else
            {
                return pair;
            }
        }

        public virtual decimal ConvertPrice(string pair, decimal price, string market, TradePriceType priceType)
        {
            string convertedPair = ChangeMarket(pair, market);
            return GetPrice(convertedPair, priceType) / price;
        }

        public TimeSpan GetTimeElapsedSinceLastTickersUpdate()
        {
            return DateTimeOffset.Now - lastTickersUpdate;
        }

        private void OnTickersUpdated(IReadOnlyCollection<KeyValuePair<string, ExchangeTicker>> updatedTickers)
        {
            if (!tickersStarted)
            {
                loggingService.Info("Ticker updates are working, good!");
                tickersStarted = true;
            }

            healthCheckService.UpdateHealthCheck(Constants.HealthChecks.TickersUpdated, $"Updates: {updatedTickers.Count}");

            lastTickersUpdate = DateTimeOffset.Now;

            foreach (var update in updatedTickers)
            {
                if (Tickers.TryGetValue(update.Key, out Ticker ticker))
                {
                    ticker.AskPrice = update.Value.Ask;
                    ticker.BidPrice = update.Value.Bid;
                    ticker.LastPrice = update.Value.Last;
                }
                else
                {
                    Tickers.TryAdd(update.Key, new Ticker
                    {
                        Pair = update.Key,
                        AskPrice = update.Value.Ask,
                        BidPrice = update.Value.Bid,
                        LastPrice = update.Value.Last
                    });

                    var market = GetPairMarket(update.Key);
                    if (!markets.Contains(market))
                    {
                        markets.Add(market);
                    }
                }
            }
        }
    }
}
