﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Cassandra.Tasks;

namespace Cassandra.Requests
{
    internal class RequestExecution<T> where T : class
    {
        // ReSharper disable once StaticMemberInGenericType
        private readonly static Logger Logger = new Logger(typeof(Session));
        private readonly RequestHandler<T> _parent;
        private readonly ISession _session;
        private readonly IRequest _request;
        private readonly Dictionary<IPEndPoint, Exception> _triedHosts = new Dictionary<IPEndPoint, Exception>();
        private Connection _connection;
        private int _retryCount;
        private volatile OperationState _operation;

        public RequestExecution(RequestHandler<T> parent, ISession session, IRequest request)
        {
            _parent = parent;
            _session = session;
            _request = request;
        }

        public void Cancel()
        {
            _operation.Cancel();
        }

        /// <summary>
        /// Starts a new execution using the current request
        /// </summary>
        /// <param name="useCurrentHost"></param>
        public void Start(bool useCurrentHost = false)
        {
            if (!useCurrentHost)
            {
                //Get a new connection from the next host
                _connection = _parent.GetNextConnection(_triedHosts);   
            }
            else if (_connection == null)
            {
                throw new DriverInternalError("No current connection set");
            }
            Send(_request, HandleResponse);
        }

        private void TryStartNew(bool useCurrentHost)
        {
            try
            {
                Start(useCurrentHost);
            }
            catch (Exception ex)
            {
                //There was an Exception before sending (probably no host is available).
                //This will mark the Task as faulted.
                HandleException(ex);
            }
        }

        /// <summary>
        /// Sends a new request using the active connection
        /// </summary>
        private void Send(IRequest request, Action<Exception, AbstractResponse> callback)
        {
            _operation = _connection.Send(request, callback);
        }

        public void HandleResponse(Exception ex, AbstractResponse response)
        {
            if (_parent.HasCompleted())
            {
                //Do nothing else, another execution finished already set the response
                return;
            }
            try
            {
                if (ex != null)
                {
                    HandleException(ex);
                    return;
                }
                if (typeof(T) == typeof(RowSet))
                {
                    HandleRowSetResult(response);
                }
                else if (typeof(T) == typeof(PreparedStatement))
                {
                    HandlePreparedResult(response);
                }
                throw new DriverInternalError(String.Format("RequestExecution with type {0} is not supported", typeof(T).FullName));
            }
            catch (Exception handlerException)
            {
                _parent.SetCompleted(handlerException);
            }
        }

        public void Retry(ConsistencyLevel? consistency, bool useCurrentHost)
        {
            _retryCount++;
            if (consistency != null && _request is ICqlRequest)
            {
                //Set the new consistency to be used for the new request
                ((ICqlRequest)_request).Consistency = consistency.Value;
            }
            Logger.Info("Retrying request: {0}", _request.GetType().Name);
            TryStartNew(useCurrentHost);
        }

        /// <summary>
        /// Gets the resulting RowSet and transitions the task to completed.
        /// </summary>
        private void HandleRowSetResult(AbstractResponse response)
        {
            var output = ((ResultResponse)response).Output;
            if (output is OutputSchemaChange)
            {
                //Schema changes need to do blocking operations
                HandleSchemaChange(output);
                return;
            }
            RowSet rs;
            if (output is OutputRows)
            {
                rs = ((OutputRows)output).RowSet;
            }
            else
            {
                if (output is OutputSetKeyspace)
                {
                    ((Session)_session).Keyspace = ((OutputSetKeyspace)output).Value;
                }
                rs = new RowSet();
            }
            _parent.SetCompleted(null, (T)(object)FillRowSet(rs, output));
        }

        private void HandleSchemaChange(IOutput output)
        {
            var result = (T)(object)FillRowSet(new RowSet(), output);
            //Wait for the schema change before returning the result
            _parent.SetCompleted(result, () => _session.Cluster.Metadata.WaitForSchemaAgreement(_connection));
        }

        /// <summary>
        /// Fills the common properties of the RowSet
        /// </summary>
        private RowSet FillRowSet(RowSet rs, IOutput output)
        {
            if (output != null && output.TraceId != null)
            {
                rs.Info.SetQueryTrace(new QueryTrace(output.TraceId.Value, _session));
            }
            rs.Info.SetTriedHosts(_triedHosts.Keys.ToList());
            if (_request is ICqlRequest)
            {
                rs.Info.SetAchievedConsistency(((ICqlRequest)_request).Consistency);
            }
            SetAutoPage(rs, _session, _parent.Statement);
            return rs;
        }

        private void SetAutoPage(RowSet rs, ISession session, IStatement statement)
        {
            rs.AutoPage = statement != null && statement.AutoPage;
            if (rs.AutoPage && rs.PagingState != null && _request is IQueryRequest && typeof(T) == typeof(RowSet))
            {
                //Automatic paging is enabled and there are following result pages
                //Set the Handler for fetching the next page.
                rs.FetchNextPage = pagingState =>
                {
                    if (_session.IsDisposed)
                    {
                        Logger.Warning("Trying to page results using a Session already disposed.");
                        return new RowSet();
                    }
                    var request = (IQueryRequest)RequestHandler<RowSet>.GetRequest(statement, session.BinaryProtocolVersion, session.Cluster.Configuration);
                    request.PagingState = pagingState;
                    var task = new RequestHandler<RowSet>(session, request, statement).Send();
                    TaskHelper.WaitToComplete(task, session.Cluster.Configuration.ClientOptions.QueryAbortTimeout);
                    return (RowSet)(object)task.Result;
                };
            }
        }

        /// <summary>
        /// Checks if the exception is either a Cassandra response error or a socket exception to retry or failover if necessary.
        /// </summary>
        private void HandleException(Exception ex)
        {
            Logger.Info("RequestHandler received exception {0}", ex.ToString());
            if (ex is PreparedQueryNotFoundException && (_parent.Statement is BoundStatement || _parent.Statement is BatchStatement))
            {
                PrepareAndRetry(((PreparedQueryNotFoundException)ex).UnknownId);
                return;
            }
            if (ex is OperationTimedOutException)
            {
                OnTimeout(ex);
                return;
            }
            if (ex is NoHostAvailableException)
            {
                //A NoHostAvailableException when trying to retrieve
                _parent.SetNoMoreHosts((NoHostAvailableException)ex, this);
                return;
            }
            if (ex is SocketException)
            {
                Logger.Verbose("Socket error " + ((SocketException)ex).SocketErrorCode);
                _parent.SetHostDown(_connection);
            }
            var decision = GetRetryDecision(ex, _parent.RetryPolicy, _parent.Statement, _retryCount);
            switch (decision.DecisionType)
            {
                case RetryDecision.RetryDecisionType.Rethrow:
                    _parent.SetCompleted(ex);
                    break;
                case RetryDecision.RetryDecisionType.Ignore:
                    //The error was ignored by the RetryPolicy
                    //Try to give a decent response
                    if (typeof(T).IsAssignableFrom(typeof(RowSet)))
                    {
                        var rs = new RowSet();
                        _parent.SetCompleted(null, (T)(object)FillRowSet(rs, null));
                    }
                    else
                    {
                        _parent.SetCompleted(null, default(T));
                    }
                    break;
                case RetryDecision.RetryDecisionType.Retry:
                    //Retry the Request using the new consistency level
                    Retry(decision.RetryConsistencyLevel, decision.UseCurrentHost);
                    break;
            }
        }

        /// <summary>
        /// It handles the steps required when there is a client-level read timeout.
        /// It is invoked by a thread from the default TaskScheduler
        /// </summary>
        private void OnTimeout(Exception ex)
        {
            Logger.Warning(ex.Message);
            if (_session == null || _connection == null)
            {
                Logger.Error("Session, Host and Connection must not be null");
                return;
            }
            var pool = ((Session)_session).GetExistingPool(_connection);
            pool.CheckHealth(_connection);
            if (_session.Cluster.Configuration.QueryOptions.RetryOnTimeout || _request is PrepareRequest)
            {
                if (_parent.HasCompleted())
                {
                    return;
                }
                TryStartNew(false);
                return;
            }
            _parent.SetCompleted(ex);
        }

        /// <summary>
        /// Gets the retry decision based on the exception from Cassandra
        /// </summary>
        public static RetryDecision GetRetryDecision(Exception ex, IRetryPolicy policy, IStatement statement, int retryCount)
        {
            var decision = RetryDecision.Rethrow();
            if (ex is SocketException)
            {
                decision = RetryDecision.Retry(null, false);
            }
            else if (ex is OverloadedException || ex is IsBootstrappingException || ex is TruncateException)
            {
                decision = RetryDecision.Retry(null, false);
            }
            else if (ex is ReadTimeoutException)
            {
                var e = (ReadTimeoutException)ex;
                decision = policy.OnReadTimeout(statement, e.ConsistencyLevel, e.RequiredAcknowledgements, e.ReceivedAcknowledgements, e.WasDataRetrieved, retryCount);
            }
            else if (ex is WriteTimeoutException)
            {
                var e = (WriteTimeoutException)ex;
                decision = policy.OnWriteTimeout(statement, e.ConsistencyLevel, e.WriteType, e.RequiredAcknowledgements, e.ReceivedAcknowledgements, retryCount);
            }
            else if (ex is UnavailableException)
            {
                var e = (UnavailableException)ex;
                decision = policy.OnUnavailable(statement, e.Consistency, e.RequiredReplicas, e.AliveReplicas, retryCount);
            }
            return decision;
        }

        /// <summary>
        /// Sends a prepare request before retrying the statement
        /// </summary>
        private void PrepareAndRetry(byte[] id)
        {
            Logger.Info(String.Format("Query {0} is not prepared on {1}, preparing before retrying executing.", BitConverter.ToString(id), _connection.Address));
            BoundStatement boundStatement = null;
            if (_parent.Statement is BoundStatement)
            {
                boundStatement = (BoundStatement)_parent.Statement;
            }
            else if (_parent.Statement is BatchStatement)
            {
                var batch = (BatchStatement)_parent.Statement;
                Func<Statement, bool> search = s => s is BoundStatement && ((BoundStatement)s).PreparedStatement.Id.SequenceEqual(id);
                boundStatement = (BoundStatement)batch.Queries.FirstOrDefault(search);
            }
            if (boundStatement == null)
            {
                throw new DriverInternalError("Expected Bound or batch statement");
            }
            var request = new PrepareRequest(_request.ProtocolVersion, boundStatement.PreparedStatement.Cql);
            if (boundStatement.PreparedStatement.Keyspace != null && _session.Keyspace != boundStatement.PreparedStatement.Keyspace)
            {
                Logger.Warning(String.Format("The statement was prepared using another keyspace, changing the keyspace temporarily to" +
                                              " {0} and back to {1}. Use keyspace and table identifiers in your queries and avoid switching keyspaces.",
                                              boundStatement.PreparedStatement.Keyspace, _session.Keyspace));
                //Use the current task scheduler to avoid blocking on a io worker thread
                Task.Factory.StartNew(() =>
                {
                    //Change the keyspace is a blocking operation
                    _connection.Keyspace = boundStatement.PreparedStatement.Keyspace;
                    Send(request, ReprepareResponseHandler);
                });
                return;
            }
            Send(request, ReprepareResponseHandler);
        }

        /// <summary>
        /// Handles the response of a (re)prepare request and retries to execute on the same connection
        /// </summary>
        private void ReprepareResponseHandler(Exception ex, AbstractResponse response)
        {
            try
            {
                if (ex != null)
                {
                    HandleException(ex);
                    return;
                }
                ValidateResult(response);
                var output = ((ResultResponse)response).Output;
                if (!(output is OutputPrepared))
                {
                    throw new DriverInternalError("Expected prepared response, obtained " + output.GetType().FullName);
                }
                Send(_request, HandleResponse);
            }
            catch (Exception exception)
            {
                //There was an issue while sending
                _parent.SetCompleted(exception);
            }
        }
        /// <summary>
        /// Creates the prepared statement and transitions the task to completed
        /// </summary>
        private void HandlePreparedResult(AbstractResponse response)
        {
            ValidateResult(response);
            var output = ((ResultResponse)response).Output;
            if (!(output is OutputPrepared))
            {
                throw new DriverInternalError("Expected prepared response, obtained " + output.GetType().FullName);
            }
            if (!(_request is PrepareRequest))
            {
                throw new DriverInternalError("Obtained PREPARED response for " + _request.GetType().FullName + " request");
            }
            var prepared = (OutputPrepared)output;
            object statement = new PreparedStatement(prepared.Metadata, prepared.QueryId, ((PrepareRequest)_request).Query, _connection.Keyspace, prepared.ResultMetadata, _session.BinaryProtocolVersion);
            _parent.SetCompleted(null, (T)statement);
        }

        private static void ValidateResult(AbstractResponse response)
        {
            if (response == null)
            {
                throw new DriverInternalError("Response can not be null");
            }
            if (!(response is ResultResponse))
            {
                throw new DriverInternalError("Excepted ResultResponse, obtained " + response.GetType().FullName);
            }
        }
    }
}
