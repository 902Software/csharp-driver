﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cassandra.Serialization
{
    internal interface ITypeSerializer
    {
        Type Type { get; }

        IColumnInfo TypeInfo { get; }

        ColumnTypeCode CqlType { get; }

        object Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo);

        byte[] Serialize(ushort protocolVersion, object obj);
    }
}
