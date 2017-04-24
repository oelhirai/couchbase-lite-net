﻿// 
// Document.cs
// 
// Author:
//     Jim Borden  <jim.borden@couchbase.com>
// 
// Copyright (c) 2017 Couchbase, Inc All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// 
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Couchbase.Lite.Internal.DB;
using Couchbase.Lite.Logging;
using Couchbase.Lite.Util;
using LiteCore;
using LiteCore.Interop;
using LiteCore.Util;

namespace Couchbase.Lite.Internal.Doc
{
    internal sealed unsafe class Document : ReadOnlyDocument, IDocument
    {
        #region Constants

        private const string Tag = nameof(Document);

        #endregion

        #region Variablesp

        private C4Database* _c4Db;
        private Database _database;
        private DictionaryObject _dict;

        #endregion

        #region Properties

        public IDatabase Database
        {
            get => _database;
            set {
                _database = value as Database;
                _c4Db = _database != null ? _database.c4db : null;
            }
        }

        public override ICollection<string> Keys => _dict.Keys;

        public new IFragment this[string key] => _dict[key];

        internal override bool IsEmpty => _dict.IsEmpty;

        #endregion

        #region Constructors

        public Document() : this(Misc.CreateGuid())
        {

        }

        public Document(string documentID)
            : base(documentID, null, new FleeceDictionary())
        {
            _dict = new DictionaryObject(Data);
        }

        public Document(IDictionary<string, object> dictionary)
            : this()
        {
            Set(dictionary);
        }

        public Document(string documentID, IDictionary<string, object> dictionary)
            : this(documentID)
        {
            Set(dictionary);
        }

        internal Document(IDatabase database, string documentID, bool mustExist)
            : base(documentID, null, new FleeceDictionary())
        {
            Database = database;
            LoadDoc(mustExist);
        }

        #endregion

        public override IDictionary<string, object> ToDictionary()
        {
            return _dict.ToDictionary();
        }

        public override bool Contains(string key)
        {
            return _dict.Contains(key);
        }

        public override object GetObject(string key)
        {
            return _dict.GetObject(key);
        }

        public override IBlob GetBlob(string key)
        {
            return _dict.GetBlob(key);
        }

        public override bool GetBoolean(string key)
        {
            return _dict.GetBoolean(key);
        }

        public override DateTimeOffset GetDate(string key)
        {
            return _dict.GetDate(key);
        }

        public override double GetDouble(string key)
        {
            return _dict.GetDouble(key);
        }

        public override int GetInt(string key)
        {
            return _dict.GetInt(key);
        }

        public override long GetLong(string key)
        {
            return _dict.GetLong(key);
        }

        public override string GetString(string key)
        {
            return _dict.GetString(key);
        }

        private void LoadDoc(bool mustExist)
        {
            var doc = (C4Document *)LiteCoreBridge.Check(err => Native.c4doc_get(_c4Db, Id, mustExist, err));
            SetC4Doc(doc);
        }

        private void Merge(IConflictResolver resolver, bool deletion)
        {
            var rawDoc = (C4Document*)LiteCoreBridge.Check(err => Native.c4doc_get(_c4Db, Id, true, err));
            FLDict* curRoot = null;
            var curBody = rawDoc->selectedRev.body;
            if (curBody.size > 0) {
                curRoot = (FLDict*) NativeRaw.FLValue_FromTrustedData(new FLSlice(curBody.buf, curBody.size));
            }

            var curDict = new FleeceDictionary(curRoot, rawDoc, Database);
            var current = new ReadOnlyDocument(Id, rawDoc, curDict);

            IReadOnlyDocument resolved;
            if (deletion) {
                // Deletion always loses a conflict:
                resolved = current;
            } else if (resolver != null) {
                // Call the custom conflict resolver:
                var baseDoc = new ReadOnlyDocument(Id, c4Doc, Data);
                var conflict = new Conflict(this, current, baseDoc, OperationType.DatabaseWrite);
                resolved = resolver.Resolve(conflict);
                if (resolved == null) {
                    throw new CouchbaseLiteException(StatusCode.Conflict);
                }
            } else {
                // Thank Jens Alfke for this variable name (lol)
                var myGgggeneration = Generation + 1;
                var theirGgggeneration = NativeRaw.c4rev_getGeneration(rawDoc->revID);
                resolved = myGgggeneration >= theirGgggeneration ? this : current;
            }

            // Now update my tate to the current C4Document and the merged/resolved properties
            if (!resolved.Equals(current)) {
                var dict = resolved.ToDictionary();
                SetC4Doc(rawDoc);
                Set(dict);
            } else {
                SetC4Doc(rawDoc);
            }
        }

        private void Save(IConflictResolver resolver, bool deletion, IDocumentModel model = null)
        {
            if(_database == null || _c4Db == null) {
                throw new InvalidOperationException("Save attempted after database was closed");
            }

            if(!_dict.HasChanges && !deletion && Exists) {
                return;
            }

            C4Document* newDoc = null;
            var endedEarly = false;
            Database.InBatch(() =>
            {
                var tmp = default(C4Document*);
                SaveInto(&tmp, deletion, model);
                if (tmp == null) {
                    Merge(resolver, deletion);
                    if (!_dict.HasChanges) {
                        endedEarly = true;
                        return;
                    }

                    SaveInto(&tmp, deletion, model);
                    if (tmp == null) {
                        throw new CouchbaseLiteException("Conflict still occuring after resolution", StatusCode.DbError);
                    }
                }

                newDoc = tmp;
            });

            if (endedEarly) {
                return;
            }

            SetC4Doc(newDoc);
        }

        [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The closure is executed synchronously")]
        private void SaveInto(C4Document** outDoc, bool deletion, IDocumentModel model = null)
        {
            var put = new C4DocPutRequest();
            using(var docId = new C4String(Id)) {
                put.docID = docId.AsC4Slice();
                if(c4Doc != null) {
                    put.history = &c4Doc->revID;
                    put.historyCount = 1;
                }

                put.save = true;

                if(deletion) {
                    put.revFlags = C4RevisionFlags.Deleted;
                }

                if(Doc.Data.ContainsBlob(this)) {
                    put.revFlags |= C4RevisionFlags.HasAttachments;
                }

                if(!deletion && !IsEmpty) {
                    var body = new FLSliceResult();
                    if (model != null) {
                        body = _database.JsonSerializer.Serialize(model);
                        put.body = body;
                    } else { 
                        body = _database.JsonSerializer.Serialize(_dict);
                        put.body = body;
                    }

                    try {
                        using (var type = new C4String(GetString("type"))) {
                            *outDoc = (C4Document*)RetryHandler.RetryIfBusy()
                                .AllowError(new C4Error(LiteCoreError.Conflict))
                                .Execute(err =>
                                {
                                    var localPut = put;
                                    localPut.docType = type.AsC4Slice();
                                    return Native.c4doc_put(_c4Db, &localPut, null, err);
                                });
                        }
                    } finally {
                        Native.FLSliceResult_Free(body);
                    }
                }
            }
           
        }

        private void SetC4Doc(C4Document* doc)
        {
            FLDict* root = null;
            var body = c4Doc->selectedRev.body;
            if(body.size > 0) {
                root = Native.FLValue_AsDict(NativeRaw.FLValue_FromTrustedData(new FLSlice(body.buf, body.size)));
            }

            c4Doc = doc;
            Data = new FleeceDictionary(root, c4Doc, _database);
            _dict = new DictionaryObject(Data);
        }

        #region Overrides

        public override string ToString()
        {
            var id = new SecureLogString(Id, LogMessageSensitivity.PotentiallyInsecure);
            return $"{GetType().Name}[{id}]";
        }

        #endregion

        #region IDocument

        public void Delete()
        {
            _threadSafety.DoLocked(() => Save(_database.ConflictResolver, true));
        }

        public bool Purge()
        {
            return _threadSafety.DoLocked(() =>
            {
                if(_database == null || _c4Db == null) {
                    throw new InvalidOperationException("Document's owning database has been closed");
                }

                if (!Exists) {
                    return false;
                }

                Database.InBatch(() =>
                {
                    LiteCoreBridge.Check(err => NativeRaw.c4doc_purgeRevision(c4Doc, C4Slice.Null, err));
                    LiteCoreBridge.Check(err => Native.c4doc_save(c4Doc, 0, err));
                });

                LoadDoc(false);
                return true;
            });
        }

        public void Save()
        {
            _threadSafety.DoLocked(() => Save(_database.ConflictResolver, false));
        }

        #endregion

        #region IModellable

        public IDictionaryObject Set(string key, object value)
        {
            _dict.Set(key, value);
            return this;
        }

        public IDictionaryObject Set(IDictionary<string, object> dictionary)
        {
            _dict.Set(dictionary);
            return this;
        }

        public IDictionaryObject Remove(string key)
        {
            _dict.Remove(key);
            return this;
        }

        IArray IDictionaryObject.GetArray(string key)
        {
            return _dict.GetArray(key);
        }

        ISubdocument IDictionaryObject.GetSubdocument(string key)
        {
            return _dict.GetSubdocument(key);
        }

        #endregion
    }
}
