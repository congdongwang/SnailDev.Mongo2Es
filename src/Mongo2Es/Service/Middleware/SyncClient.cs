﻿using Mongo2Es.ElasticSearch;
using Mongo2Es.Log;
using Mongo2Es.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mongo2Es.Middleware
{
    public class SyncClient
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private Mongo.MongoClient client;
        private readonly string database = "Mongo2Es";
        private readonly string collection = "SyncNode";
        private readonly string[] opArr = new string[] { "i", "u", "d" };
        private System.Timers.Timer nodesRefreshTimer;
        private ConcurrentDictionary<string, SyncNode> scanNodesDic = new ConcurrentDictionary<string, SyncNode>();
        private ConcurrentDictionary<string, SyncNode> tailNodesDic = new ConcurrentDictionary<string, SyncNode>();

        public SyncClient() { }

        public SyncClient(string mongoUrl)
        {
            this.client = new Mongo.MongoClient(mongoUrl);
        }

        public void Run()
        {
            nodesRefreshTimer = new System.Timers.Timer
            {
                Interval = 3 * 1000 * 5
            };
            nodesRefreshTimer.Elapsed += (sender, args) =>
            {
                var nodes = client.GetCollectionData<SyncNode>(database, collection);

                #region Scan
                var scanNodes = nodes.Where(x => (x.Status == SyncStatus.WaitForScan || x.Status == SyncStatus.ProcessScan) && x.Switch != SyncSwitch.Stop);
                var scanIds = scanNodes.Select(x => x.ID);
                foreach (var node in scanNodes)
                {
                    if (!scanNodesDic.ContainsKey(node.ID))
                    {
                        ThreadPool.QueueUserWorkItem(ExcuteScanProcess, node);
                        LogUtil.LogInfo(logger, $"全量同步节点({node.Name})进入线程池", node.ID);
                    }

                    if (scanNodesDic.TryGetValue(node.ID, out SyncNode oldNode))
                    {
                        if (!oldNode.ToString().Equals(node.ToString()))
                            scanNodesDic.AddOrUpdate(node.ID, node, (key, oldValue) => oldValue = node);
                    }
                    else
                    {
                        scanNodesDic.AddOrUpdate(node.ID, node, (key, oldValue) => oldValue = node);
                    }
                }

                foreach (var key in scanNodesDic.Keys.Except(scanIds))
                {
                    scanNodesDic.Remove(key, out SyncNode node);
                }
                #endregion

                #region Tail
                var tailNodes = nodes.Where(x => (x.Status == SyncStatus.WaitForTail || x.Status == SyncStatus.ProcessTail) && x.Switch != SyncSwitch.Stop);
                var tailIds = tailNodes.Select(x => x.ID);

                foreach (var item in tailNodes)
                {
                    if (!tailNodesDic.ContainsKey(item.ID))
                    {
                        ThreadPool.QueueUserWorkItem(ExcuteTailProcess, item);
                        LogUtil.LogInfo(logger, $"增量同步节点({item.Name})进入线程池", item.ID);
                    }

                    if (tailNodesDic.TryGetValue(item.ID, out SyncNode oldNode))
                    {
                        if (!oldNode.ToString().Equals(item.ToString()))
                            tailNodesDic.AddOrUpdate(item.ID, item, (key, oldValue) => oldValue = item);
                    }
                    else
                    {
                        tailNodesDic.AddOrUpdate(item.ID, item, (key, oldValue) => oldValue = item);
                    }
                }

                foreach (var key in tailNodesDic.Keys.Except(tailIds))
                {
                    tailNodesDic.Remove(key, out SyncNode node);
                }
                #endregion
            };
            nodesRefreshTimer.Disposed += (sender, args) =>
            {
                logger.Info("nodes更新线程退出");
            };
            nodesRefreshTimer.Start();
        }

        /// <summary>
        /// 全表同步
        /// </summary>
        /// <param name="node"></param>
        private void ExcuteScanProcess(object obj)
        {
            var node = obj as SyncNode;
            var mongoClient = new Mongo.MongoClient(node.MongoUrl);
            var esClient = new EsClient(node.ID, node.EsUrl);

            LogUtil.LogInfo(logger, $"全量同步({node.Name})节点开始", node.ID);

            try
            {
                if (!esClient.IsIndexExsit(node.Index))
                {
                    LogUtil.LogInfo(logger, $"检测索引{node.Index}未创建，正在创建...", node.ID);
                    if (esClient.CreateIndex(node.Index))
                    {
                        LogUtil.LogInfo(logger, $"索引{node.Index}创建成功.", node.ID);
                    }
                    else
                    {
                        throw new Exception($"索引{node.Index}创建失败.");
                    }
                }

                LogUtil.LogInfo(logger, $"正在更新类型{node.Type}的mapping", node.ID);
                if (esClient.PutMapping(node.Index, node.Type, node.Mapping))
                {
                    LogUtil.LogInfo(logger, $"类型{node.Type}的mapping更新成功", node.ID);
                }
                else
                {
                    throw new Exception($"类型{node.Type}的mapping更新失败");
                }

                // 记下当前Oplog的位置
                var currentOplog = mongoClient.GetCollectionData<BsonDocument>("local", "oplog.rs", "{}", "{$natural:-1}", 1).FirstOrDefault();
                node.OperTailSign = currentOplog["ts"].AsBsonTimestamp.Timestamp;
                node.OperTailSignExt = currentOplog["ts"].AsBsonTimestamp.Increment;
                client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                          Update.Set("OperTailSign", node.OperTailSign).Set("OperTailSignExt", node.OperTailSignExt).ToBsonDocument());

                var filter = "{}";
                var data = mongoClient.GetCollectionData<BsonDocument>(node.DataBase, node.Collection, filter, limit: 1);
                if (node.IsLog && !String.IsNullOrWhiteSpace(node.OperScanSign))
                {
                    filter = data.Last()["_id"].IsObjectId ?
                          $"{{'_id':{{ $gt:new ObjectId('{node.OperScanSign}')}}}}"
                          : $"{{'_id':{{ $gt:{node.OperScanSign}}}}}";
                    data = mongoClient.GetCollectionData<BsonDocument>(node.DataBase, node.Collection, filter, limit: 1000);
                }

                while (data.Count() > 0)
                {
                    //System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                    //sw.Start();
                    if (esClient.InsertBatchDocument(node.Index, node.Type, IBatchDocuemntHandle(data, node.ProjectFields, node.LinkField)))
                    {
                        LogUtil.LogInfo(logger, $"节点({node.Name}),文档(count:{data.Count()})写入ES成功", node.ID);

                        //if (string.IsNullOrWhiteSpace(node.OperScanSign))
                        //{
                        //    if (esClient.SetIndexRefreshAndReplia(node.Index, "-1", 0))
                        //    {
                        //        LogUtil.LogInfo(logger, $"ES 索引写入性能优化成功", node.ID);
                        //    }
                        //    else
                        //    {
                        //        LogUtil.LogInfo(logger, $"ES 索引写入性能优化失败", node.ID);
                        //    }
                        //}

                        node.Status = SyncStatus.ProcessScan;
                        node.OperScanSign = data.Last()["_id"].ToString();
                        //node.OperTailSign = client.GetTimestampFromDateTime(DateTime.UtcNow);

                        client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                            Update.Set("Status", node.Status).Set("OperScanSign", node.OperScanSign).ToBsonDocument());

                        filter = data.Last()["_id"].IsObjectId ?
                            $"{{'_id':{{ $gt:new ObjectId('{node.OperScanSign}')}}}}"
                            : $"{{'_id':{{ $gt:{node.OperScanSign}}}}}";
                        data = mongoClient.GetCollectionData<BsonDocument>(node.DataBase, node.Collection, filter, limit: 1000);
                    }
                    else
                    {
                        LogUtil.LogInfo(logger, $"节点({node.Name}),文档(count:{data.Count()})写入ES失败,需手动重置", node.ID);
                        node.Status = SyncStatus.ScanException;
                        node.Switch = SyncSwitch.Stop;
                        client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                            Update.Set("Status", node.Status).Set("Switch", node.Switch).ToBsonDocument());
                        return;
                    }

                    if (scanNodesDic.TryGetValue(node.ID, out SyncNode oldNode))
                    {
                        node = oldNode;
                        if (node.Switch == SyncSwitch.Stoping)
                        {
                            node.Switch = SyncSwitch.Stop;
                            client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                               Update.Set("Switch", node.Switch).ToBsonDocument());
                            LogUtil.LogInfo(logger, $"全量同步节点({node.Name})已停止, scan线程停止", node.ID);
                            return;
                        }
                    }

                    //sw.Stop();
                    //LogUtil.LogInfo(logger, sw.ElapsedMilliseconds.ToString(), node.ID);
                }

                if (esClient.SetIndexRefreshAndReplia(node.Index))
                {
                    LogUtil.LogInfo(logger, $"ES 索引{node.Index}副本及刷新时间还原成功", node.ID);
                }
                else
                {
                    LogUtil.LogInfo(logger, $"ES 索引{node.Index}副本及刷新时间还原失败，可手动还原", node.ID);
                }

                //if (node.IsLog)
                //{
                //    // 记下当前Oplog的位置
                //    currentOplog = mongoClient.GetCollectionData<BsonDocument>("local", "oplog.rs", "{}", "{$natural:-1}", 1).FirstOrDefault();
                //    node.OperTailSign = currentOplog["ts"].AsBsonTimestamp.Timestamp;
                //    node.OperTailSignExt = currentOplog["ts"].AsBsonTimestamp.Increment;
                //    client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                //              Update.Set("OperTailSign", node.OperTailSign).Set("OperTailSignExt", node.OperTailSignExt).ToBsonDocument());
                //}

                node.Status = SyncStatus.WaitForTail;
                client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                         Update.Set("Status", node.Status).ToBsonDocument());
                LogUtil.LogInfo(logger, $"索引{node.Index}正在等待增量同步...", node.ID);
            }
            catch (Exception ex)
            {
                node.Status = SyncStatus.ScanException;
                node.Switch = SyncSwitch.Stop;
                client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                    Update.Set("Status", node.Status).Set("Switch", node.Switch).ToBsonDocument());


                LogUtil.LogError(logger, ex.ToString(), node.ID);
            }

            LogUtil.LogInfo(logger, $"全量同步({node.Name})节点结束", node.ID);
        }

        /// <summary>
        /// 增量同步
        /// </summary>
        /// <param name="obj"></param>
        private void ExcuteTailProcess(object obj)
        {
            var node = obj as SyncNode;
            var mongoClient = new Mongo.MongoClient(node.MongoUrl);
            var esClient = new EsClient(node.ID, node.EsUrl);

            LogUtil.LogInfo(logger, $"增量同步({node.Name})节点开始", node.ID);

            // 动态规划
            int maxCount = 1000;
            int minElapsedMilliseconds = 150;
            int maxElapsedMilliseconds = 3500;
            bool bulkSwitch = false;
            DateTime lastDataTime = DateTime.Now;
            List<EsData> esDatas = new List<EsData>();

            try
            {
                node.Status = SyncStatus.ProcessTail;
                client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                  Update.Set("Status", node.Status).ToBsonDocument());

                while (true)
                {
                    using (var cursor = mongoClient.TailMongoOpLogs($"{node.DataBase}.{node.Collection}", node.OperTailSign, node.OperTailSignExt))
                    {
                        try
                        {
                            #region version 1
                            /*
                            foreach (var opLog in cursor.ToEnumerable())
                            {
                                if (!opArr.Contains(opLog["op"].AsString)) continue;

                                if (tailNodesDic.TryGetValue(node.ID, out SyncNode oldNode))
                                {
                                    node = oldNode;
                                    if (node.Switch == SyncSwitch.Stoping)
                                    {
                                        node.Switch = SyncSwitch.Stop;
                                        client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                                                            Update.Set("Switch", node.Switch).ToBsonDocument());
                                        LogUtil.LogInfo(logger, $"增量同步节点({node.Name})已停止, tail线程停止", node.ID);
                                        return;
                                    }
                                }

                                bool flag = true;
                                switch (opLog["op"].AsString)
                                {
                                    case "i":
                                        var iid = string.IsNullOrWhiteSpace(node.LinkField) ? opLog["o"]["_id"].ToString() : opLog["o"][node.LinkField].ToString();
                                        var idoc = IDocuemntHandle(opLog["o"].AsBsonDocument, node.ProjectFields);
                                        if (idoc.Names.Count() > 0)
                                        {
                                            if (string.IsNullOrWhiteSpace(node.LinkField))
                                            {
                                                if (esClient.InsertDocument(node.Index, node.Type, iid, idoc))
                                                    LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{iid}）写入ES成功", node.ID);
                                                else
                                                {
                                                    flag = false;
                                                    LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{iid}）写入ES失败", node.ID);
                                                }
                                            }
                                            else
                                            {
                                                idoc.Remove("id");
                                                if (esClient.UpdateDocument(node.Index, node.Type, iid, idoc))
                                                    LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{iid}）更新ES成功", node.ID);
                                                else
                                                {
                                                    flag = false;
                                                    LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{iid}）更新ES失败", node.ID);
                                                }
                                            }
                                        }
                                        break;
                                    case "u":
                                        var uid = opLog["o2"]["_id"].ToString();
                                        var udoc = opLog["o"].AsBsonDocument;

                                        if (!string.IsNullOrWhiteSpace(node.LinkField))
                                        {
                                            var filter = opLog["o2"]["_id"].IsObjectId ? $"{{'_id':new ObjectId('{uid}')}}" : $"{{'_id':{uid}}}";
                                            var dataDetail = mongoClient.GetCollectionData<BsonDocument>(node.DataBase, node.Collection, filter, limit: 1).FirstOrDefault();
                                            if (dataDetail == null || !dataDetail.Contains(node.LinkField)) continue;
                                            uid = dataDetail[node.LinkField].ToString();
                                        }

                                        if (udoc.Contains("$unset"))
                                        {
                                            var unsetdoc = udoc["$unset"].AsBsonDocument;
                                            udoc.Remove("$unset");

                                            var delFields = UnsetDocHandle(unsetdoc, node.ProjectFields);
                                            if (delFields.Count > 0)
                                            {
                                                if (esClient.DeleteField(node.Index, node.Type, uid, delFields))
                                                {
                                                    LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{uid}）删除ES字段({string.Join(",", delFields)})成功", node.ID);
                                                }
                                                else
                                                {
                                                    flag = false;
                                                    LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{uid}）删除ES字段({string.Join(",", delFields)})失败", node.ID);
                                                    break;
                                                }
                                            }
                                        }

                                        udoc = UDocuemntHandle(udoc, node.ProjectFields);
                                        if (udoc.Names.Count() > 0)
                                        {
                                            if (esClient.UpdateDocument(node.Index, node.Type, uid, udoc))
                                                LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{uid}）更新ES成功", node.ID);
                                            else
                                            {
                                                flag = false;
                                                LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{uid}）更新ES失败", node.ID);
                                            }
                                        }

                                        break;
                                    case "d":
                                        var did = opLog["o"]["_id"].ToString();
                                        if (string.IsNullOrWhiteSpace(node.LinkField))
                                        {
                                            if (esClient.DeleteDocument(node.Index, node.Type, did))
                                                LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{did}）删除ES成功", node.ID);
                                            else
                                            {
                                                flag = false;
                                                LogUtil.LogInfo(logger, $"节点({node.Name}),文档（id:{did}）删除ES失败", node.ID);
                                            }
                                        }
                                        break;
                                    default:
                                        break;
                                }

                                if (flag)
                                {
                                    node.OperTailSign = opLog["ts"].AsBsonTimestamp.Timestamp;
                                    node.OperTailSignExt = opLog["ts"].AsBsonTimestamp.Increment;
                                    client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                                    Update.Set("OperTailSign", node.OperTailSign).Set("OperTailSignExt", node.OperTailSignExt).ToBsonDocument());
                                }
                                else
                                {
                                    node.Status = SyncStatus.TailException;
                                    node.Switch = SyncSwitch.Stop;
                                    client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                                                      Update.Set("Status", node.Status).Set("Switch", node.Switch).ToBsonDocument());

                                    return;
                                }
                            }*/
                            #endregion

                            #region version now
                            foreach (var opLog in cursor.ToEnumerable())
                            {
                                //LogUtil.LogInfo(logger, "开始", node.ID);                              
                                //System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                                //sw.Start();

                                if (tailNodesDic.TryGetValue(node.ID, out SyncNode oldNode))
                                {
                                    node = oldNode;
                                    if (node.Switch == SyncSwitch.Stoping)
                                    {
                                        node.Switch = SyncSwitch.Stop;
                                        client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                                                            Update.Set("Switch", node.Switch).ToBsonDocument());
                                        LogUtil.LogInfo(logger, $"增量同步节点({node.Name})已停止, tail线程停止", node.ID);
                                        return;
                                    }
                                }

                                if (!opLog["ns"].AsString.Equals($"{node.DataBase}.{node.Collection}")) continue;
                                if (!opArr.Contains(opLog["op"].AsString)) continue;
                                switch (opLog["op"].AsString)
                                {
                                    case "i":
                                        var iid = string.IsNullOrWhiteSpace(node.LinkField) ? opLog["o"]["_id"].ToString() : opLog["o"][node.LinkField].ToString();
                                        var idoc = IDocuemntHandle(opLog["o"].AsBsonDocument, node.ProjectFields);
                                        if (idoc.Names.Count() > 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(node.LinkField)) idoc.Remove("id");
                                            esDatas.Add(new EsData()
                                            {
                                                Oper = "insert",
                                                ID = iid,
                                                Data = idoc,
                                                Time = DateTime.Now
                                            });
                                        }
                                        break;
                                    case "u":
                                        var uid = opLog["o2"]["_id"].ToString();
                                        var udoc = opLog["o"].AsBsonDocument;

                                        if (!string.IsNullOrWhiteSpace(node.LinkField))
                                        {
                                            var filter = opLog["o2"]["_id"].IsObjectId ? $"{{'_id':new ObjectId('{uid}')}}" : $"{{'_id':{uid}}}";
                                            var dataDetail = mongoClient.GetCollectionData<BsonDocument>(node.DataBase, node.Collection, filter, limit: 1).FirstOrDefault();
                                            if (dataDetail == null || !dataDetail.Contains(node.LinkField)) continue;
                                            uid = dataDetail[node.LinkField].ToString();
                                        }

                                        if (udoc.Contains("$unset"))
                                        {
                                            var unsetdoc = udoc["$unset"].AsBsonDocument;
                                            udoc.Remove("$unset");

                                            var delFields = UnsetDocHandle(unsetdoc, node.ProjectFields);
                                            if (delFields.Count > 0)
                                            {
                                                esDatas.Add(new EsData()
                                                {
                                                    Oper = "delFields",
                                                    ID = uid,
                                                    Data = delFields,
                                                    Time = DateTime.Now
                                                });
                                            }
                                        }

                                        udoc = UDocuemntHandle(udoc, node.ProjectFields);
                                        if (udoc.Names.Count() > 0)
                                        {
                                            esDatas.Add(new EsData()
                                            {
                                                Oper = "update",
                                                ID = uid,
                                                Data = udoc,
                                                Time = DateTime.Now
                                            });
                                        }

                                        break;
                                    case "d":
                                        var did = opLog["o"]["_id"].ToString();
                                        if (string.IsNullOrWhiteSpace(node.LinkField))
                                        {
                                            esDatas.Add(new EsData()
                                            {
                                                Oper = "delete",
                                                ID = did,
                                                Time = DateTime.Now
                                            });
                                        }
                                        break;
                                    default:
                                        break;
                                }


                                if (esDatas.Count > 0)
                                {
                                    if (bulkSwitch)
                                    {
                                        //LogUtil.LogInfo(logger, (DateTime.Now - esDatas.First().Time).TotalMilliseconds.ToString(), node.ID);
                                        if (esDatas.Count >= maxCount)
                                        {

                                        }
                                        else if ((DateTime.Now - esDatas.First().Time).TotalMilliseconds >= maxElapsedMilliseconds)
                                        {
                                            bulkSwitch = false;
                                            //LogUtil.LogInfo(logger, $"节点({node.Name})增量同步降级为单条同步", node.ID);
                                        }
                                        else
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        if ((DateTime.Now - lastDataTime).TotalMilliseconds <= minElapsedMilliseconds)
                                        {
                                            bulkSwitch = true;
                                            //LogUtil.LogInfo(logger, $"节点({node.Name})增量同步升级为批量同步", node.ID);
                                        }
                                    }

                                    lastDataTime = DateTime.Now;
                                    // bulk
                                    if (esClient.InsertBatchDocument(node.Index, node.Type, BatchDocuemntHandle(esDatas)))
                                    {
                                        LogUtil.LogInfo(logger, $"节点({node.Name}),文档(count:{esDatas.Count})更新ES成功", node.ID);

                                        esDatas.Clear();

                                        node.OperTailSign = opLog["ts"].AsBsonTimestamp.Timestamp;
                                        node.OperTailSignExt = opLog["ts"].AsBsonTimestamp.Increment;
                                        client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                                        Update.Set("OperTailSign", node.OperTailSign).Set("OperTailSignExt", node.OperTailSignExt).ToBsonDocument());
                                    }
                                    else
                                    {
                                        LogUtil.LogInfo(logger, $"节点({node.Name}),文档(count:{esDatas.Count})更新ES失败,需手动重置", node.ID);
                                        node.Status = SyncStatus.TailException;
                                        node.Switch = SyncSwitch.Stop;
                                        client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                                                          Update.Set("Status", node.Status).Set("Switch", node.Switch).ToBsonDocument());

                                        return;
                                    }
                                }

                                //sw.Stop();
                                //LogUtil.LogInfo(logger, sw.ElapsedMilliseconds.ToString(), node.ID);
                                //LogUtil.LogInfo(logger, "结束", node.ID);
                            }
                            #endregion
                        }
                        catch (MongoExecutionTimeoutException ex)
                        {
                            // Nohandle with MongoExecutionTimeoutException
                            if (node != null)
                            {
                                LogUtil.LogWarn(logger, $"同步({node.Name})节点异常：{ex}", node.ID);
                                mongoClient = new Mongo.MongoClient(node.MongoUrl);
                            }
                        }
                        catch (MongoCommandException ex)
                        {
                            // Nohandle with MongoExecutionTimeoutException
                            if (node != null)
                            {
                                LogUtil.LogWarn(logger, $"同步({node.Name})节点异常：{ex}", node.ID);
                                mongoClient = new Mongo.MongoClient(node.MongoUrl);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                node.Status = SyncStatus.TailException;
                node.Switch = SyncSwitch.Stop;
                client.UpdateCollectionData<SyncNode>(database, collection, node.ID,
                  Update.Set("Status", node.Status).Set("Switch", node.Switch).ToBsonDocument());


                LogUtil.LogError(logger, $"同步({node.Name})节点异常：{ex}", node.ID);
            }

            LogUtil.LogInfo(logger, $"增量同步({node.Name})节点结束", node.ID);
        }

        #region Doc Handle

        /// <summary>
        /// 处理id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private BsonValue HandleID(BsonValue id)
        {
            return id.IsObjectId ? id.ToString() : id;
        }

        /// <summary>
        /// 处理数组（key转小写）
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private BsonArray HandleArray(BsonArray docs, string projectFields)
        {
            BsonArray newDoc = new BsonArray();
            foreach (var doc in docs)
            {
                if (doc.IsBsonArray)
                {
                    newDoc.Add(HandleArray(doc.AsBsonArray, projectFields));
                }
                else if (doc.IsBsonDocument)
                {
                    newDoc.Add(HandleDoc(doc.AsBsonDocument, projectFields));
                }
                else
                {
                    newDoc.AddRange(docs);
                    break;
                }
            }

            return newDoc;
        }

        /// <summary>
        /// 处理文档（key转小写）
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private BsonDocument HandleDoc(BsonDocument doc, string projectFields)
        {
            projectFields = projectFields ?? "";
            var fieldsArr = projectFields.Split(",").ToList().ConvertAll(x => x.Split('.')[0].Trim());
            var names = doc.Names.ToList();

            if (fieldsArr.Count(x => string.IsNullOrWhiteSpace(x)) == fieldsArr.Count)
            {
                fieldsArr = names;
                fieldsArr.Remove("_id");
            }

            BsonDocument newDoc = new BsonDocument();
            if (doc.Contains("_id"))
            {
                newDoc.Add(new BsonElement("ID", HandleID(doc["_id"])));
            }

            foreach (var name in names)
            {
                if (fieldsArr.Contains(name))
                {
                    var subProjectFields = string.Join(',', projectFields.Split(",").Where(s => s.StartsWith(name, StringComparison.CurrentCultureIgnoreCase)).ToList().ConvertAll(x => x.Contains(".") ? x.Substring(x.IndexOf(".") + 1).Trim() : null));
                    if (doc[name].IsBsonArray)
                    {
                        newDoc.AddRange(new BsonDocument(name, HandleArray(doc[name].AsBsonArray, subProjectFields)));
                    }
                    else if (doc[name].IsBsonDocument)
                    {
                        newDoc.AddRange(new BsonDocument(name, HandleDoc(doc[name].AsBsonDocument, subProjectFields)));
                    }
                    else
                    {
                        if (doc[name].IsBsonDateTime || doc[name].IsValidDateTime)
                        {
                            newDoc.Add(new BsonElement(name, doc[name].ToString()));
                        }
                        else
                        {
                            newDoc.Add(new BsonElement(name, doc[name]));
                        }
                    }
                }
            }

            return newDoc;
        }

        /// <summary>
        /// 插入文档处理
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private BsonDocument IDocuemntHandle(BsonDocument doc, string projectFields)
        {
            return HandleDoc(doc, projectFields);
        }

        /// <summary>
        /// 插入文档处理
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private List<string> IDocuemntHandle(IEnumerable<BsonDocument> docs, string projectFields)
        {
            var handDocs = new List<string>();
            foreach (var doc in docs)
            {
                var _doc = doc["o"].AsBsonDocument;
                handDocs.Add(new
                {
                    index = new
                    {
                        _id = doc["_id"].ToString()
                    }
                }.ToJson());

                var newDoc = HandleDoc(_doc, projectFields);

                if (newDoc.Names.Count() > 0)
                    handDocs.Add(newDoc.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
            }


            return handDocs;
        }

        /// <summary>
        /// 批量插入文档处理
        /// </summary>
        /// <param name="docs"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private List<string> IBatchDocuemntHandle(IEnumerable<BsonDocument> docs, string projectFields, string linkfield)
        {
            var handDocs = new List<string>();
            foreach (var doc in docs)
            {
                if (string.IsNullOrWhiteSpace(linkfield))
                {
                    handDocs.Add(new
                    {
                        index = new
                        {
                            _id = doc["_id"].ToString()
                        }
                    }.ToJson());
                }
                else
                {
                    handDocs.Add(new
                    {
                        update = new
                        {
                            _id = HandleID(doc[linkfield])
                        }
                    }.ToJson());
                    doc.Remove(linkfield);

                    doc.Remove("_id");
                }

                var newDoc = HandleDoc(doc, projectFields);

                if (string.IsNullOrWhiteSpace(linkfield))
                    handDocs.Add(newDoc.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
                else
                    handDocs.Add(new { doc = newDoc }.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
            }


            return handDocs;
        }

        /// <summary>
        /// 批量操作文档处理
        /// </summary>
        /// <param name="esDatas"></param>
        /// <returns></returns>
        private List<string> BatchDocuemntHandle(IEnumerable<EsData> esDatas)
        {
            var handDocs = new List<string>();
            foreach (var doc in esDatas)
            {
                switch (doc.Oper)
                {
                    case "insert":
                        handDocs.Add(new
                        {
                            index = new
                            {
                                _id = doc.ID
                            }
                        }.ToJson());
                        handDocs.Add((doc.Data as BsonDocument).ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
                        break;
                    case "delFields":
                    case "update":
                        handDocs.Add(new
                        {
                            update = new
                            {
                                _id = doc.ID
                            }
                        }.ToJson());
                        if (doc.Oper.Equals("delFields"))
                        {
                            var fieldScripts = (doc.Data as List<string>).ConvertAll(x => $"ctx._source.remove(\"{x}\")");
                            handDocs.Add(new { script = string.Join(";", fieldScripts) }.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
                        }
                        else
                        {
                            handDocs.Add(new { doc = (doc.Data as BsonDocument) }.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
                        }
                        break;
                    case "delete":
                        handDocs.Add(new
                        {
                            delete = new
                            {
                                _id = doc.ID
                            }
                        }.ToJson());
                        break;
                    default:
                        break;
                }
            }

            return handDocs;
        }

        /// <summary>
        /// 更新文档处理
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private BsonDocument UDocuemntHandle(BsonDocument doc, string projectFields)
        {
            if (doc.Contains("$set"))
            {
                doc = doc["$set"].AsBsonDocument;
            }

            return HandleDoc(doc, projectFields);
        }

        /// <summary>
        /// $unset操作处理
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="projectFields"></param>
        /// <returns></returns>
        private List<string> UnsetDocHandle(BsonDocument doc, string projectFields)
        {
            projectFields = projectFields ?? "";
            if (projectFields == "") return doc.Names.ToList();

            var fieldsArr = projectFields.Split(",").ToList().ConvertAll(x => x.Trim());
            // fieldsArr.Remove("_id");

            return doc.Names.Intersect(fieldsArr).ToList();
        }
        #endregion

        #region GetMapping
        public string GetMapping(string mongo, string db, string col, string projectfields, string linkfield, string es)
        {
            string mapping = "";

            var index = $"temp{DateTime.Now.ToString("yyyyMMddHHmmss")}";
            var esClient = new Mongo2Es.ElasticSearch.EsClient("System", es);
            if (!esClient.IsIndexExsit(index))
            {
                if (esClient.CreateIndex(index))
                {
                    var mongoclient = new Mongo2Es.Mongo.MongoClient(mongo);
                    var lastData = mongoclient.GetCollectionData<BsonDocument>(db, col, "{}", "{'_id':-1}", 1);

                    var handDocs = new List<string>();
                    foreach (var doc in lastData)
                    {

                        handDocs.Add(new
                        {
                            index = new
                            {
                                _id = doc["_id"].ToString()
                            }
                        }.ToJson());

                        if (!string.IsNullOrWhiteSpace(linkfield))
                        {
                            doc.Remove(linkfield);
                            doc.Remove("_id");
                        }

                        var newDoc = HandleDoc(doc, projectfields);

                        if (string.IsNullOrWhiteSpace(linkfield))
                            handDocs.Add(newDoc.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
                        else
                            handDocs.Add(new { doc = newDoc }.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.Strict }));
                    }

                    if (esClient.InsertBatchDocument(index, index, handDocs))
                    {
                        mapping = esClient.GetMapping(index).ToString();
                    }
                }

                if (!esClient.DeleteIndex(index))
                {
                    mapping += $"索引{index}删除失败，请手动删除";
                }
            }


            return mapping;
        }
        #endregion
    }
}
