﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventDrivenThinking.App.Configuration.EventStore;
using EventDrivenThinking.EventInference.Abstractions;
using EventDrivenThinking.EventInference.Abstractions.Read;
using EventDrivenThinking.EventInference.Core;
using EventDrivenThinking.EventInference.EventHandlers;
using EventDrivenThinking.EventInference.Models;
using EventDrivenThinking.EventInference.Projections;
using EventDrivenThinking.EventInference.Schema;
using EventDrivenThinking.EventInference.Subscriptions;
using EventDrivenThinking.Integrations.EventStore;
using EventDrivenThinking.Logging;
using EventDrivenThinking.Ui;
using EventDrivenThinking.Utils;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;
using Prism.Events;
using Serilog;

namespace EventDrivenThinking.EventInference.QueryProcessing
{
    /// <summary>
    /// Understands how to process events that related to this TModel
    /// Understands how to switch partitions.
    /// Can execute many queries.
    /// Under the hood have one model.
    /// If model changes, query results changes automatically
    /// When two queries are executed, both have results, both will get updates.
    /// Query Engine tracks query-results and how they will be affected by new events.
    /// If Query returns data from the model, then changes also will be propagated.
    /// </summary>
    /// <typeparam name="TModel"></typeparam>l
    public class QueryEngine<TModel> : IQueryEngine<TModel> where TModel : IModel
    {
        class QueryEventHandler<TQuery, TEvent, TResult> : IEventHandler<TEvent> 
            where TEvent : IEvent
            where TQuery : IQuery<TModel, TResult>
        {
            private readonly IQueryHandler<TQuery, TModel, TResult> _queryHandler;
            private readonly IEventHandler<TEvent> _projectionHandler;
            private readonly TModel _model;
            private readonly LiveQuery<TQuery,TResult> _liveQuery;

            public QueryEventHandler(IQueryHandler<TQuery, TModel, TResult> queryHandler, IEventHandler<TEvent> projectionHandler,
                TModel model, LiveQuery<TQuery, TResult> liveQuery)
            {
                _queryHandler = queryHandler;
                _projectionHandler = projectionHandler;
                _model = model;
                _liveQuery = liveQuery;
            }

            public async Task Execute(EventMetadata m, TEvent ev)
            {
                await _projectionHandler.Execute(m, ev);

                var result = _queryHandler.Execute(_model, _liveQuery.Query);

                _liveQuery.OnResult(result);
            }
        }
        class QueryEngineHandlerFactory<TQuery, TResult> : EventHandlerFactoryBase 
            where TQuery : IQuery<TModel, TResult>
        {
            private readonly QueryEngine<TModel> _queryEngine;
            private readonly ProjectionStreamInfo _streamInfo;
            private readonly LiveQuery<TQuery, TResult> _liveQuery;
            private readonly IProjectionSchema _projectionSchema;

            public QueryEngineHandlerFactory(QueryEngine<TModel> queryEngine,
                ProjectionStreamInfo streamInfo,
                LiveQuery<TQuery, TResult> liveQuery, IProjectionSchema projectionSchema)
            {
                _queryEngine = queryEngine;
                _streamInfo = streamInfo;
                _liveQuery = liveQuery;
                _projectionSchema = projectionSchema;
                SupportedEventTypes = new TypeCollection(_projectionSchema.Events);
            }

            public override TypeCollection SupportedEventTypes { get; }
            public override IEventHandler<TEvent> CreateHandler<TEvent>()
            {
                var queryHandler = _queryEngine.CreateQueryHandler<TQuery, TResult>();
                var projectionHandler = _queryEngine.CreateEventHandler<TEvent>(_projectionSchema);
                return new QueryEventHandler<TQuery, TEvent, TResult>(queryHandler, projectionHandler,
                    _queryEngine.GetModel(), _liveQuery);
            }
        }

        private IEventHandler<TEvent> CreateEventHandler<TEvent>(IProjectionSchema projectionSchema)
            where TEvent : IEvent
        {
            var projectionHandlerType = typeof(ProjectionEventHandler<,>).MakeGenericType(projectionSchema.Type, typeof(TEvent));
            return (IEventHandler<TEvent>)ActivatorUtilities.CreateInstance(_serviceProvider, projectionHandlerType, GetModel());
        }

        class ProjectionStreamInfo
        {
            public Guid? PartitionId { get; private set; }
            public bool IsRootStream { get; private set; }
            public ISubscription Subscription { get; set; }
            public readonly ConcurrentDictionary<IQuery, ILiveQuery> AssociatedQueries;
            public ProjectionStreamInfo(Guid partitionId, bool isRootPartition = false)
            {
                IsRootStream = isRootPartition;
                PartitionId = partitionId;
                AssociatedQueries = new ConcurrentDictionary<IQuery, ILiveQuery>();
            }
        }
        internal class LiveQuery<TQuery, TResult> : ILiveQuery, ILiveResult<TResult>
            where TQuery: IQuery<TModel, TResult>
        {
            private readonly Action<TQuery> _onDispose;
            
            IQuery ILiveQuery.Query => Query;
            public LiveQueryStatus Status { get; private set; }
            object ILiveResult.Result => Result;

            public event EventHandler ResultUpdated;
            public event EventHandler StatusChanged;
            object ILiveQuery.Result => Result;
            
            public QueryOptions Options { get; }
            
            public TQuery Query { get; }
            public TResult Result { get; private set; }
            public Guid? PartitionId { get; }
            public LiveQuery(TQuery query, Guid? partitionId,
                IQuerySchema schema,
                Action<TQuery> onDispose,
                QueryOptions options)
            {
                _onDispose = onDispose;
                Query = query;
                Options = options;
                PartitionId = partitionId;
            }

            public void OnResult(TResult result)
            {
                this.Result = result;
                Status = LiveQueryStatus.Running;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            public void OnUpdate(TResult result)
            {
                this.Result.CopyFrom(result);
                ResultUpdated?.Invoke(this, EventArgs.Empty);
            }

            public void Dispose()
            {
                _onDispose(this.Query);
                Status = LiveQueryStatus.Disposed;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        interface ILiveQuery
        {
            IQuery Query { get; }
            object Result { get; }
            //IQueryResult QueryResult { get; }
            Guid? PartitionId { get; }
            QueryOptions Options { get; }
        }
        private static readonly ILogger Log = LoggerFactory.For<QueryEngine<TModel>>();

        private ConcurrentDictionary<IQuery, ILiveQuery> _liveQueries;
        private ConcurrentDictionary<Guid,ProjectionStreamInfo> _partitions;

        private TModel _model;
        private readonly IServiceProvider _serviceProvider;
        private readonly IQuerySchemaRegister _querySchemaRegister;
        private readonly IProjectionSchemaRegister _projectionSchemaRegister;
        
        private readonly IProjectionSubscriptionController _subscriptionController;
        
        private readonly Guid _rootPartitionId = Guid.NewGuid();
        public QueryEngine(IServiceProvider serviceProvider, 
            IQuerySchemaRegister querySchemaRegister,
            IProjectionSchemaRegister projectionSchemaRegister,
            IProjectionSubscriptionController subscriptionController,
            DispatcherQueue dispatcherQueue)
        {
            _serviceProvider = serviceProvider;
            _querySchemaRegister = querySchemaRegister;
            _projectionSchemaRegister = projectionSchemaRegister;
            _subscriptionController = subscriptionController;
            _dispatcherQueue = dispatcherQueue;

            _liveQueries = new ConcurrentDictionary<IQuery, ILiveQuery>();
            _partitions = new ConcurrentDictionary<Guid, ProjectionStreamInfo>();
            
            Log.Debug("QueryEngine for model {modelName} created.", typeof(TModel).Name);
        }

        public TModel GetModel()
        {
            if (_model == null)
            {
                _model = ActivatorUtilities.GetServiceOrCreateInstance<TModel>(_serviceProvider);
            }
            return _model;
        }


        private IQueryHandler<TQuery, TModel, TResult> CreateQueryHandler<TQuery, TResult>()
        {
            return ActivatorUtilities.GetServiceOrCreateInstance<IQueryHandler<TQuery, TModel, TResult>>(
                _serviceProvider);
        }

        public async Task<ILiveResult<TResult>> Execute<TQuery, TResult>(TQuery query, QueryOptions options = null)
            where TQuery : IQuery<TModel, TResult>
            where TResult: class
        {
            var schema = _querySchemaRegister.GetByQueryType(typeof(TQuery));
            var projectionSchema = _projectionSchemaRegister.FindByModelType(typeof(TModel));

            
            var queryParitioner =  _serviceProvider.GetService<IEnumerable<IQueryPartitioner<TQuery>>>()
                .SingleOrDefault();
            
            var partitionId = queryParitioner?.CalculatePartition(GetModel(), query);
            
            LiveQuery<TQuery, TResult> liveQuery = new LiveQuery<TQuery,  TResult>(query, partitionId, schema, OnQueryDispose, options);

            if (partitionId.HasValue)
            {
                if (!_partitions.TryGetValue(partitionId.Value, out ProjectionStreamInfo streamInfo))
                {
                    streamInfo = new ProjectionStreamInfo(partitionId.Value);
                    // this needs to wait for all events to go though the wire.
                    AsyncAutoResetEvent subscriptionReady = new AsyncAutoResetEvent(false);


                    var handlerFactory = CreateHandlerFactory(streamInfo, liveQuery, projectionSchema);
                    await _subscriptionController.SubscribeHandlers(projectionSchema, handlerFactory, partitionId.Value);

                    //streamInfo.Subscription = await _modelProjectionSubscriber.SubscribeToStream(
                    //    async events => await EnqueueEvents<TQuery, TResult>(streamInfo, schema.ProjectionType, events),
                    //    subscription => { subscriptionReady.Set(); },
                    //    partitionId.Value);
                    
                    //_partitions.TryAdd(partitionId.Value, streamInfo);

                    //Log.Debug("Live query {queryName} subscribed for events in partition {partitionId} stream of projection {projectionName}", typeof(TQuery).Name, partitionId,schema.ProjectionType.Name);
                    //subscriptionReady.Wait();
                    //Thread.Sleep(100);
                }
                
                // we have subscribed to partition before. Just need to return result and execute the handler.
                var handler = _serviceProvider.GetService<IQueryHandler<TQuery, TModel, TResult>>();
                var result = handler.Execute(GetModel(), query);
                liveQuery.OnResult(result);

                streamInfo.AssociatedQueries.TryAdd(liveQuery.Query, liveQuery);
                _liveQueries.TryAdd(liveQuery.Query, liveQuery);
                
            }
            else
            {
                if (!_partitions.TryGetValue(_rootPartitionId, out ProjectionStreamInfo streamInfo))
                {
                    streamInfo = new ProjectionStreamInfo(_rootPartitionId, true);

                    var handlerFactory = CreateHandlerFactory(streamInfo, liveQuery, projectionSchema);
                    // this needs to wait for all events to go though the wire.
                    //AsyncAutoResetEvent subscriptionReady = new AsyncAutoResetEvent(false);

                    await _subscriptionController.SubscribeHandlers(projectionSchema, handlerFactory);

                    //streamInfo.Subscription = await _subscriptionController.SubscribeToStream(
                    //    async events => await EnqueueEvents<TQuery, TResult>(streamInfo, schema.ProjectionType, events),
                    //    subscription => { subscriptionReady.Set(); },
                    //    null);

                    _partitions.TryAdd(_rootPartitionId, streamInfo);

                    //subscriptionReady.Wait();
                    Thread.Sleep(100);
                    Log.Debug("Live query {queryName} subscribed for events in root stream of projection {projectionName}", typeof(TQuery).Name, schema.ProjectionType.Name);
                }

                // we have subscribed to partition before. Just need to return result and execute the handler.
                var handler = _serviceProvider.GetService<IQueryHandler<TQuery, TModel, TResult>>();
                var result = handler.Execute(GetModel(), query);
                liveQuery.OnResult(result);

                streamInfo.AssociatedQueries.TryAdd(liveQuery.Query, liveQuery);
                _liveQueries.TryAdd(liveQuery.Query, liveQuery);

            }




            return liveQuery;
        }

        private IEventHandlerFactory CreateHandlerFactory<TQuery, TResult>(ProjectionStreamInfo streamInfo,
            LiveQuery<TQuery, TResult> liveQuery, IProjectionSchema projectionSchema) where TQuery : IQuery<TModel, TResult>
        {
            return new QueryEngineHandlerFactory<TQuery,TResult>(this, streamInfo, liveQuery, projectionSchema);
        }

        private readonly DispatcherQueue _dispatcherQueue;
        async Task EnqueueEvents<TQuery, TResult>(ProjectionStreamInfo streamInfo, Type projectionType,
            (EventMetadata, IEvent)[] events)
            where TQuery : IQuery<TModel, TResult>
        {
            _dispatcherQueue.Enqueue(() => OnEvents<TQuery, TResult>(streamInfo, projectionType, events));
        }
       

        private void OnQueryDispose<TQuery>(TQuery obj) where TQuery : IQuery
        {
            if (_liveQueries.TryRemove(obj, out ILiveQuery liveQuery))
            {
                if (liveQuery.PartitionId.HasValue)
                    _partitions[liveQuery.PartitionId.Value].AssociatedQueries.TryRemove(liveQuery.Query, out ILiveQuery tmp);
                else _partitions[_rootPartitionId].AssociatedQueries.TryRemove(liveQuery.Query, out ILiveQuery tmp);
            }
        }
        
        async Task OnEvents<TQuery, TResult>(ProjectionStreamInfo streamInfo, Type projectionType, (EventMetadata, IEvent)[] ev)
        where TQuery:IQuery<TModel, TResult>
        {
            if(streamInfo.IsRootStream)
                Log.Debug("Root partition received events on {projectionType}.", projectionType.Name);
            else
                Log.Debug("Partition {streamInfo.PartitionId} received events on {projectionName}",streamInfo.PartitionId, projectionType.Name);

            var projection = (IProjection<TModel>) ActivatorUtilities.CreateInstance(_serviceProvider, projectionType, GetModel());
            await projection.Execute(ev);
            
            foreach (LiveQuery<TQuery, TResult> i in streamInfo.AssociatedQueries.Values.OfType<LiveQuery<TQuery, TResult>>())
            {
                Log.Debug("Found live query to update {queryName}", i.Query.GetType().Name);
                var handler = _serviceProvider.GetService<IQueryHandler<TQuery, TModel, TResult>>();
                var result = handler.Execute(GetModel(), i.Query);

                i.OnUpdate(result);
            }
        }

        
    }
}
