﻿using System.Collections.Generic;

namespace BethFile
{
    public static class Helpers
    {
        public static List<BethesdaRecord> ExtractRecords(BethesdaFile file) => ExtractRecords(file, null);

        public static List<BethesdaRecord> ExtractRecords(BethesdaFile file, HashSet<uint> ids)
        {
            var vis = new ExtractRecordsVisitor(ids);
            vis.Visit(file);
            return vis.Records;
        }

        private sealed class ExtractRecordsVisitor : BethesdaFileVisitor
        {
            private readonly HashSet<uint> ids;

            internal ExtractRecordsVisitor(HashSet<uint> ids)
            {
                this.ids = ids;
            }

            internal List<BethesdaRecord> Records { get; } = new List<BethesdaRecord>();

            protected override void OnRecord(BethesdaRecord record)
            {
                if (this.ids?.Contains(record.Id) != false)
                {
                    this.Records.Add(record);
                }

                base.OnRecord(record);
            }
        }
    }
}