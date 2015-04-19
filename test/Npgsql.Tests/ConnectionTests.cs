// project created on 30/11/2002 at 22:00
//
// Author:
//     Francisco Figueiredo Jr. <fxjrlists@yahoo.com>
//
//    Copyright (C) 2002 The Npgsql Development Team
//    npgsql-general@gborg.postgresql.org
//    http://gborg.postgresql.org/project/npgsql/projdisplay.php
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using System.Data;
using System.Resources;
using NUnit.Framework;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NpgsqlTypes;

namespace Npgsql.Tests
{
    [TestFixture]
    public class ConnectionTests : TestBase
    {
        public ConnectionTests(string backendVersion) : base(backendVersion) { }

        [Test, Description("Makes sure the connection goes through the proper state lifecycle")]
        //[Timeout(5000)]
        public void BasicLifecycle()
        {
            using (var conn = new NpgsqlConnection(ConnectionString))
            {
                bool eventOpen = false, eventClosed = false;
                conn.StateChange += (s, e) =>
                {
                    if (e.OriginalState == ConnectionState.Closed && e.CurrentState == ConnectionState.Open)
                        eventOpen = true;
                    if (e.OriginalState == ConnectionState.Open && e.CurrentState == ConnectionState.Closed)
                        eventClosed = true;
                };

                Assert.That(conn.State, Is.EqualTo(ConnectionState.Closed));
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Closed));

                // TODO: Connecting state?

                conn.Open();
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Open));
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open));
                Assert.That(conn.Connector.State, Is.EqualTo(ConnectorState.Ready));
                Assert.That(eventOpen, Is.True);

                using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    reader.Read();
                    Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open | ConnectionState.Fetching));
                    Assert.That(conn.State, Is.EqualTo(ConnectionState.Open));
                    Assert.That(conn.Connector.State, Is.EqualTo(ConnectorState.Fetching));
                }

                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open));
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Open));
                Assert.That(conn.Connector.State, Is.EqualTo(ConnectorState.Ready));

                using (var cmd = new NpgsqlCommand("SELECT pg_sleep(1)", conn))
                {
                    var exitFlag = false;
                    var pollingTask = Task.Factory.StartNew(() =>
                    {
                        while (true)
                        {
                            if (exitFlag) {
                                Assert.Fail("Connection did not reach the Executing state");
                            }
                            if (conn.Connector.State == ConnectorState.Executing)
                            {
                                Assert.That(conn.FullState & ConnectionState.Executing, Is.Not.EqualTo(0));
                                Assert.That(conn.State, Is.EqualTo(ConnectionState.Open));
                                return;
                            }
                        }
                    });
                    cmd.ExecuteNonQuery();
                    exitFlag = true;
                    pollingTask.Wait();
                }

                conn.Close();
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Closed));
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Closed));
                Assert.That(eventClosed, Is.True);

                conn.Open();
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Open));
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open));
                Assert.That(conn.Connector.State, Is.EqualTo(ConnectorState.Ready));

                // TODO: Broken, when implemented
            }
        }

        #region Connection Errors

        [Test]
        [TestCase(true,  TestName = "Pooled")]
        [TestCase(false, TestName = "NonPooled")]
        public void ConnectionRefused(bool pooled)
        {
            using (var conn = new NpgsqlConnection("Server=127.0.0.1;Port=44444;User Id=x;Password=y" + (pooled ? "" : ";Pooling=false"))) {
                Assert.That(() => conn.Open(), Throws.Exception.TypeOf<SocketException>());
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Closed));
            }
        }

        [Test]
        [Ignore("Fails in a non-determinstic manner and only on the build server... investigate...")]
        public void InvalidUserId()
        {
            using (var conn = new NpgsqlConnection(ConnectionString + ";userid=npgsql_tes;pooling=false"))
            {
                Assert.That(conn.Open, Throws.Exception
                    .TypeOf<NpgsqlException>()
                    .With.Property("Code").EqualTo("28P01")
                );
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Closed));
            }
        }

        [Test]
        public void InvalidConnectionString()
        {
            Assert.That(() => new NpgsqlConnection("Server=127.0.0.1;User Id=npgsql_tests;Pooling:false"),
                Throws.Exception.TypeOf<ArgumentException>());
        }

        [Test, Description("Connects with a bad password to ensure the proper error is thrown")]
        public void AuthenticationFailure()
        {
            var badConnString = Regex.Replace(ConnectionString, @"Password=\w+", "Password=bad_password");
            using (var conn = new NpgsqlConnection(badConnString))
            {
                Assert.That(() => conn.Open(), Throws.Exception
                    .TypeOf<NpgsqlException>()
                    .With.Property("Code").EqualTo("28P01")
                );
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Closed));
            }
        }

        #endregion

        [Test]
        public void ChangeDatabase()
        {
            Conn.ChangeDatabase("template1");
            var command = new NpgsqlCommand("select current_database()", Conn);
            var result = (String)command.ExecuteScalar();
            Assert.AreEqual("template1", result);
        }

        [Test]
        public void ChangeDatabaseTestConnectionCache()
        {
            using (var conn1 = new NpgsqlConnection(ConnectionString))
            using (var conn2 = new NpgsqlConnection(ConnectionString))
            {
                //    connection 1 change database
                conn1.Open();
                conn1.ChangeDatabase("template1");
                var command = new NpgsqlCommand("select current_database()", conn1);
                var db1 = (String)command.ExecuteScalar();
                Assert.AreEqual("template1", db1);

                //    connection 2 's database should not changed, so should different from conn1
                conn2.Open();
                command = new NpgsqlCommand("select current_database()", conn2);
                var db2 = (String)command.ExecuteScalar();
                Assert.AreNotEqual(db1, db2);
            }
        }

        [Test]
        public void NestedTransaction()
        {
            Conn.BeginTransaction();
            Assert.That(() => Conn.BeginTransaction(), Throws.TypeOf<NotSupportedException>());
        }

        [Test]
        public void SequencialTransaction()
        {
            Conn.BeginTransaction().Rollback();
            Conn.BeginTransaction();
        }

        [Test, Description("Tests closing a connector while a reader is open")]
        [TestCase(true, TestName = "Pooled")]
        [TestCase(false, TestName = "NonPooled")]
        public void CloseDuringRead(bool pooled)
        {
            var conn = new NpgsqlConnection(ConnectionString + (pooled ? "" : ";Pooling=false"));
            conn.Open();
            using (var cmd = new NpgsqlCommand("SELECT 1", conn))
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read();
                conn.Close();
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Closed));
                Assert.That(reader.IsClosed);
            }

            // Make sure we can reuse the pooled connector
            conn.Open();
            Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open));
            Assert.That(ExecuteScalar("SELECT 1"), Is.EqualTo(1));
        }

        [Test]
        public void SearchPathSupport()
        {
            using (var conn = new NpgsqlConnection(ConnectionString + ";searchpath=public"))
            {
                conn.Open();
                var c = new NpgsqlCommand("show search_path", conn);
                var searchpath = (String)c.ExecuteScalar();
                //Note, public is no longer implicitly added to paths, so this is no longer "public, public".
                Assert.AreEqual("public", searchpath);
            }
        }

        [Test]
        public void ConnectorNotInitializedException1000581()
        {
            var command = new NpgsqlCommand();
            command.CommandText = @"SELECT 123";

            for (var i = 0; i < 2; i++)
            {
                using (var connection = new NpgsqlConnection(ConnectionString))
                {
                    connection.Open();
                    command.Connection = connection;
                    command.Transaction = connection.BeginTransaction();
                    command.ExecuteScalar();
                    command.Transaction.Commit();
                }
            }
        }

        [Test]
        [Ignore]
        public void NpgsqlErrorRepro1()
        {
            throw new NotImplementedException();
#if WHAT_TO_DO_WITH_THIS
            using (var connection = new NpgsqlConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var largeObjectMgr = new LargeObjectManager(connection);
                    try
                    {
                        var largeObject = largeObjectMgr.Open(-1, LargeObjectManager.READWRITE);
                        transaction.Commit();
                    }
                    catch
                    {
                        // ignore the LO failure
                    }
                } // *1* sometimes it throws "System.NotSupportedException: This stream does not support seek operations"

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM pg_database";
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.IsTrue(reader.Read()); // *2* this fails if the initial connection is used
                    }
                }
            } // *3* sometimes it throws "System.NotSupportedException: This stream does not support seek operations"
#endif
        }

        [Test]
        public void Bug1011001()
        {
            //[#1011001] Bug in NpgsqlConnectionStringBuilder affects on cache and connection pool

            var csb1 = new NpgsqlConnectionStringBuilder(@"Server=server;Port=5432;User Id=user;Password=passwor;Database=database;");
            var cs1 = csb1.ToString();
            var csb2 = new NpgsqlConnectionStringBuilder(cs1);
            var cs2 = csb2.ToString();
            Assert.IsTrue(cs1 == cs2);
        }

        [Test]
        public void NpgsqlErrorRepro2()
        {
#if WHAT_TO_DO_WITH_THIS
            var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            var transaction = connection.BeginTransaction();
            var largeObjectMgr = new LargeObjectManager(connection);
            try
            {
                var largeObject = largeObjectMgr.Open(-1, LargeObjectManager.READWRITE);
                transaction.Commit();
            }
            catch
            {
                // ignore the LO failure
                try
                {
                    transaction.Dispose();
                }
                catch
                {
                    // ignore dispose failure
                }
                try
                {
                    connection.Dispose();
                }
                catch
                {
                    // ignore dispose failure
                }
            }

            using (connection = new NpgsqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM pg_database";
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.IsTrue(reader.Read());
                        // *1* this fails if the connection for the pool happens to be the bad one from above
                        Assert.IsTrue(!String.IsNullOrEmpty((string)reader["datname"]));
                    }
                }
            }
#endif
        }

        [Test]
        public void GetSchemaForeignKeys()
        {
            var dt = Conn.GetSchema("ForeignKeys");
            Assert.IsNotNull(dt);
        }

        [Test]
        public void GetSchemaParameterMarkerFormats()
        {
            ExecuteNonQuery(@"INSERT INTO data (field_int4) VALUES (4)");
            var dt = Conn.GetSchema("DataSourceInformation");
            var parameterMarkerFormat = (string)dt.Rows[0]["ParameterMarkerFormat"];

            using (var connection = new NpgsqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    const String parameterName = "p_field_int4";
                    command.CommandText = "SELECT * FROM data WHERE field_int4=" + String.Format(parameterMarkerFormat, parameterName);
                    command.Parameters.Add(new NpgsqlParameter(parameterName, 4));
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.IsTrue(reader.Read());
                        // This is OK, when no exceptions are occurred.
                    }
                }
            }
        }

        [Test]
        public void GetConnectionState()
        {
            // Test created to PR #164

            NpgsqlConnection c = new NpgsqlConnection();
            c.Dispose();
            Assert.AreEqual(ConnectionState.Closed, c.State);
        }

        [Test]
        public void ChangeApplicationNameWithConnectionStringBuilder()
        {
            // Test for issue #165 on github.
            NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder();
            builder.ApplicationName = "test";
        }

        [Test, Description("Makes sure notices are probably received and emitted as events")]
        public void Notice()
        {
            ExecuteNonQuery(@"
                 CREATE OR REPLACE FUNCTION emit_notice() RETURNS VOID AS
                    'BEGIN RAISE NOTICE ''testnotice''; END;'
                 LANGUAGE 'plpgsql';
            ");

            NpgsqlNotice notice = null;
            NoticeEventHandler action = (sender, args) => notice = args.Notice;
            Conn.Notice += action;
            try
            {
                ExecuteNonQuery("SELECT emit_notice()");
                Assert.That(notice, Is.Not.Null, "No notice was emitted");
                Assert.That(notice.MessageText, Is.EqualTo("testnotice"));
                Assert.That(notice.Severity, Is.EqualTo(ErrorSeverity.Notice));
            }
            finally
            {
                Conn.Notice -= action;
            }
        }

        [Test, Description("Makes sure that ssl_renegotiation_limit is always 0, renegotiation is buggy")]
        public void NoSslRenegotiation()
        {
            Assert.That(ExecuteScalar("SHOW ssl_renegotiation_limit"), Is.EqualTo("0"));
            ExecuteNonQuery("DISCARD ALL");
            Assert.That(ExecuteScalar("SHOW ssl_renegotiation_limit"), Is.EqualTo("0"));
        }

        [Test, Description("Makes sure that concurrent use of the connection throws an exception")]
        public void ConcurrentUse()
        {
            using (var cmd = new NpgsqlCommand("SELECT 1", Conn))
            using (cmd.ExecuteReader())
                Assert.That(() => ExecuteScalar("SELECT 1", Conn), Throws.Exception.TypeOf<InvalidOperationException>());
        }

        #region GetSchema

        [Test]
        public void GetSchema()
        {
            using (NpgsqlConnection c = new NpgsqlConnection())
            {
                DataTable metaDataCollections = c.GetSchema();
                Assert.IsTrue(metaDataCollections.Rows.Count > 0, "There should be one or more metadatacollections returned. No connectionstring is required.");
            }
        }

        [Test]
        public void GetSchemaWithDbMetaDataCollectionNames()
        {
            DataTable metaDataCollections = Conn.GetSchema(System.Data.Common.DbMetaDataCollectionNames.MetaDataCollections);
            Assert.IsTrue(metaDataCollections.Rows.Count > 0, "There should be one or more metadatacollections returned.");
            foreach (DataRow row in metaDataCollections.Rows)
            {
                var collectionName = (string)row["CollectionName"];
                //checking this collection
                if (collectionName != System.Data.Common.DbMetaDataCollectionNames.MetaDataCollections)
                {
                    var collection = Conn.GetSchema(collectionName);
                    Assert.IsNotNull(collection, "Each of the advertised metadata collections should work");
                }
            }
        }

        [Test]
        public void GetSchemaWithRestrictions()
        {
            DataTable metaDataCollections = Conn.GetSchema(System.Data.Common.DbMetaDataCollectionNames.Restrictions);
            Assert.IsTrue(metaDataCollections.Rows.Count > 0, "There should be one or more Restrictions returned.");
        }

        [Test]
        public void GetSchemaWithReservedWords()
        {
            DataTable metaDataCollections = Conn.GetSchema(System.Data.Common.DbMetaDataCollectionNames.ReservedWords);
            Assert.IsTrue(metaDataCollections.Rows.Count > 0, "There should be one or more ReservedWords returned.");
        }

        #endregion

        [Test, Description("Makes sure the preload connstring param triggers the right exception")]
        [ExpectedException(typeof(NotSupportedException))]
        public void PreloadReaderNotSupported()
        {
            using (var conn = new NpgsqlConnection(ConnectionString + ";PRELOADREADER=true")) {
                conn.Open();
            }
        }
    }
}