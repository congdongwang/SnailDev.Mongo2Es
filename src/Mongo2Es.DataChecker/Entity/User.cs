﻿using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Repository.IEntity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Mongo2Es.DataChecker
{
    [Serializable]
    [BsonIgnoreExtraElements]
    public class User : IEntity<long>
    {
        [BsonId]
        public long ID { get; set; }

        public long MallID { get; set; }
    }
}
