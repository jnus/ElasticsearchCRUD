﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Threading;
using ElasticsearchCRUD.Tracing;
using Newtonsoft.Json.Linq;

namespace ElasticsearchCRUD
{
	public class ElasticSearchContext : IDisposable 
	{
		private readonly IElasticSearchMappingResolver _elasticSearchMappingResolver;

		private const string BatchOperationPath = "/_bulk";
		private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
		private readonly Uri _elasticsearchUrlBatch;
		private readonly HttpClient _client = new HttpClient();

		private readonly List<Tuple<EntityContextInfo, object>> _entityPendingChanges = new List<Tuple<EntityContextInfo, object>>();

		public ITraceProvider TraceProvider = new NullTraceProvider();
		private readonly string _connectionString;
		private bool _includeChildObjectsInDocument;

		public bool AllowDeleteForIndex { get; set; }

		public ElasticSearchContext(string connectionString, IElasticSearchMappingResolver elasticSearchMappingResolver, bool includeChildObjectsInDocument = true)
		{
			_includeChildObjectsInDocument = includeChildObjectsInDocument;
			_connectionString = connectionString;
			TraceProvider.Trace(TraceEventType.Verbose, "new ElasticSearchContext with connection string: {0}", connectionString);
			_elasticSearchMappingResolver = elasticSearchMappingResolver;
			_elasticsearchUrlBatch = new Uri(new Uri(connectionString), BatchOperationPath);
		
		}

		public void AddUpdateEntity(object entity, object id)
		{
			TraceProvider.Trace(TraceEventType.Verbose,"Adding entity: {0}, {1} to pending list", entity.GetType().Name, id );
			_entityPendingChanges.Add(new Tuple<EntityContextInfo, object>(new EntityContextInfo { Id = id.ToString(), EntityType = entity.GetType() }, entity));
		}

		public void DeleteEntity<T>(object id)
		{
			TraceProvider.Trace(TraceEventType.Verbose,"Request to delete entity with id: {0}, Type: {1}, adding to pending list", id, typeof(T).Name);
			_entityPendingChanges.Add(new Tuple<EntityContextInfo, object>(new EntityContextInfo { Id = id.ToString(), DeleteEntity = true, EntityType = typeof(T)}, null));
		}

		public ResultDetails<string> SaveChanges()
		{
			try
			{
				var task = Task.Run(() => SaveChangesAsync());
				task.Wait();
				return task.Result;
			}
			catch (AggregateException ae)
			{
				ae.Handle(x =>
				{
					TraceProvider.Trace(TraceEventType.Warning, x, "SaveChanges");
					if (x is ElasticsearchCrudException || x is HttpRequestException)
					{
						throw x;
					}

					throw new ElasticsearchCrudException(x.Message);
				});
			}

			TraceProvider.Trace(TraceEventType.Error, "Unknown error for SaveChanges");
			throw new ElasticsearchCrudException("Unknown error for SaveChanges");
		}

		public async Task<ResultDetails<string>> SaveChangesAsync()
		{
			TraceProvider.Trace(TraceEventType.Verbose, "Save changes to Elasticsearch started");
			var resultDetails = new ResultDetails<string> { Status = HttpStatusCode.InternalServerError };

			if (_entityPendingChanges.Count == 0)
			{
				resultDetails = new ResultDetails<string> {Status = HttpStatusCode.OK, Description = "Nothing to save"};
				return resultDetails;
			}

			try
			{
				string serializedEntities;
				using (var serializer = new ElasticsearchSerializer(TraceProvider, _elasticSearchMappingResolver, _includeChildObjectsInDocument))
				{
					serializedEntities = serializer.Serialize(_entityPendingChanges);
				}
				var content = new StringContent(serializedEntities);
				TraceProvider.Trace(TraceEventType.Verbose, "sending bulk request: {0}", serializedEntities);
				TraceProvider.Trace(TraceEventType.Verbose, "Request HTTP POST uri: {0}", _elasticsearchUrlBatch.AbsoluteUri);
				content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
				var response = await _client.PostAsync(_elasticsearchUrlBatch, content, _cancellationTokenSource.Token).ConfigureAwait(true);

				resultDetails.Status = response.StatusCode;
				if (response.StatusCode != HttpStatusCode.OK)
				{
					TraceProvider.Trace(TraceEventType.Warning, "SaveChangesAsync response status code: {0}, {1}", response.StatusCode, response.ReasonPhrase);
					if (response.StatusCode == HttpStatusCode.BadRequest)
					{
						var errorInfo = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
						resultDetails.Description = errorInfo;
						return resultDetails;
					}

					return resultDetails;
				}

				var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				var responseObject = JObject.Parse(responseString);
				TraceProvider.Trace(TraceEventType.Verbose, "response: {0}", responseString);
				string errors = String.Empty; 
				var items = responseObject["items"];
				if (items != null)
				{
					foreach (var item in items)
					{
						if (item["delete"] != null && item["delete"]["status"] != null)
						{
							if (item["delete"]["status"].ToString() == "404")
							{
								resultDetails.Status = HttpStatusCode.NotFound;
								errors = errors + string.Format("Delete failed for item: {0}, {1}, {2}  :", item["delete"]["_index"],
									item["delete"]["_type"], item["delete"]["_id"]);
							}
						}
					}	
				}

				resultDetails.Description = responseString;
				resultDetails.PayloadResult = serializedEntities;

				if (!String.IsNullOrEmpty(errors))
				{
					TraceProvider.Trace(TraceEventType.Warning, errors);
					throw new ElasticsearchCrudException(errors);
				}

				return resultDetails;
			}
			catch (OperationCanceledException oex)
			{
				TraceProvider.Trace(TraceEventType.Warning, oex, "Get Request OperationCanceledException: {0}", oex.Message);
				resultDetails.Description = "OperationCanceledException";
				return resultDetails;
			}

			finally
			{
				_entityPendingChanges.Clear();
			}
		}

		public T GetEntity<T>(object entityId)
		{
			try
			{
				var task = Task.Run(() => GetEntityAsync<T>(entityId));
				task.Wait();
				if (task.Result.Status == HttpStatusCode.NotFound)
				{
					TraceProvider.Trace(TraceEventType.Warning, "HttpStatusCode.NotFound");
					throw new ElasticsearchCrudException("HttpStatusCode.NotFound");
				}
				return task.Result.PayloadResult;
			}
			catch (AggregateException ae)
			{
				ae.Handle(x =>
				{
					TraceProvider.Trace(TraceEventType.Warning, x, "GetEntity {0}, {1}", typeof(T), entityId);
					if (x is ElasticsearchCrudException || x is HttpRequestException)
					{
						throw x;
					}

					throw new ElasticsearchCrudException(x.Message);
				});
			}

			TraceProvider.Trace(TraceEventType.Error, "Unknown error for GetEntity {0}, Type {1}", entityId, typeof(T));
			throw new ElasticsearchCrudException(string.Format("Unknown error for GetEntity {0}, Type {1}", entityId, typeof(T)));
		}

		public async Task<ResultDetails<T>> GetEntityAsync<T>(object entityId)
		{
			TraceProvider.Trace(TraceEventType.Verbose, "Request for select entity with id: {0}, Type: {1}", entityId, typeof(T));
			var resultDetails = new ResultDetails<T>{Status=HttpStatusCode.InternalServerError};
			try
			{
				var elasticSearchMapping = _elasticSearchMappingResolver.GetElasticSearchMapping(typeof(T));
				var elasticsearchUrlForEntityGet = string.Format("{0}/{1}/{2}/", _connectionString, elasticSearchMapping.GetIndexForType(typeof(T)), elasticSearchMapping.GetDocumentType(typeof(T)));
				var uri = new Uri(elasticsearchUrlForEntityGet + entityId);
				TraceProvider.Trace(TraceEventType.Verbose, "Request HTTP GET uri: {0}", uri.AbsoluteUri);
				var response = await _client.GetAsync(uri,  _cancellationTokenSource.Token).ConfigureAwait(false);

				resultDetails.Status = response.StatusCode;
				if (response.StatusCode != HttpStatusCode.OK)
				{
					TraceProvider.Trace(TraceEventType.Warning, "GetEntityAsync response status code: {0}, {1}", response.StatusCode, response.ReasonPhrase);
					if (response.StatusCode == HttpStatusCode.BadRequest)
					{
						var errorInfo = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
						resultDetails.Description = errorInfo;
						return resultDetails;
					}
				}

				var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				TraceProvider.Trace(TraceEventType.Verbose, "Get Request response: {0}", responseString);
				var responseObject = JObject.Parse(responseString);

				var source = responseObject["_source"];
				if (source != null)
				{
					var result = _elasticSearchMappingResolver.GetElasticSearchMapping(typeof(T)).ParseEntity(source, typeof(T));
					resultDetails.PayloadResult = (T)result;
				}

				return resultDetails;
			}
			catch (OperationCanceledException oex)
			{
				TraceProvider.Trace(TraceEventType.Verbose, oex, "Get Request OperationCanceledException: {0}", oex.Message);
				return resultDetails;
			}
		}

		public async Task<ResultDetails<T>> DeleteIndexAsync<T>()
		{
			if (!AllowDeleteForIndex)
			{
				TraceProvider.Trace(TraceEventType.Error, "Delete Index is not activated for this context. If ou want to activate it, set the AllowDeleteForIndex property of the context");
				throw new ElasticsearchCrudException("Delete Index is not activated for this context. If ou want to activate it, set the AllowDeleteForIndex property of the context");
			}
			TraceProvider.Trace(TraceEventType.Verbose, "Request to delete complete index for Type: {0}", typeof(T));

			var resultDetails = new ResultDetails<T> { Status = HttpStatusCode.InternalServerError };
			try
			{
				var elasticSearchMapping = _elasticSearchMappingResolver.GetElasticSearchMapping(typeof(T));
				var elasticsearchUrlForIndexDelete = string.Format("{0}{1}", _connectionString, elasticSearchMapping.GetIndexForType(typeof(T)));
				var uri = new Uri(elasticsearchUrlForIndexDelete );
				TraceProvider.Trace(TraceEventType.Warning, "Request HTTP Delete uri: {0}", uri.AbsoluteUri);
				var response = await _client.DeleteAsync(uri, _cancellationTokenSource.Token).ConfigureAwait(false);

				resultDetails.Status = response.StatusCode;
				if (response.StatusCode != HttpStatusCode.OK)
				{
					TraceProvider.Trace(TraceEventType.Warning, "DeleteIndexAsync response status code: {0}, {1}", response.StatusCode, response.ReasonPhrase);
					if (response.StatusCode == HttpStatusCode.BadRequest)
					{
						var errorInfo = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
						resultDetails.Description = errorInfo;
						return resultDetails;
					}
				}

				var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				TraceProvider.Trace(TraceEventType.Verbose, "Delete Index Request response: {0}", responseString);
				resultDetails.Description = responseString;

				return resultDetails;
			}
			catch (OperationCanceledException oex)
			{
				TraceProvider.Trace(TraceEventType.Warning,  oex, "Get Request OperationCanceledException: {0}", oex.Message);
				return resultDetails;
			}
		}

		public void Dispose()
		{
			if (_client != null)
			{
				_client.Dispose();
			}
		}
	}
}
