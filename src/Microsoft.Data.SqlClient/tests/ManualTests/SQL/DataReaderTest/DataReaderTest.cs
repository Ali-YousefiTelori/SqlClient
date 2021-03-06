// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading;
using Xunit;

namespace Microsoft.Data.SqlClient.ManualTesting.Tests
{
    public static class DataReaderTest
    {
        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public static void LoadReaderIntoDataTableToTestGetSchemaTable()
        {
            using (SqlConnection connection = new SqlConnection(DataTestUtility.TCPConnectionString))
            {
                connection.Open();
                var dt = new DataTable();
                using (SqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "select 3 as [three], 4 as [four]";
                    // Datatables internally call IDataReader.GetSchemaTable()
                    dt.Load(command.ExecuteReader());
                    Assert.Equal(2, dt.Columns.Count);
                    Assert.Equal("three", dt.Columns[0].ColumnName);
                    Assert.Equal("four", dt.Columns[1].ColumnName);
                    Assert.Equal(1, dt.Rows.Count);
                    Assert.Equal(3, (int)dt.Rows[0][0]);
                    Assert.Equal(4, (int)dt.Rows[0][1]);
                }
            }
        }

        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public static void MultiQuerySchema()
        {
            using (SqlConnection connection = new SqlConnection(DataTestUtility.TCPConnectionString))
            {
                connection.Open();

                using (SqlCommand command = connection.CreateCommand())
                {
                    // Use multiple queries
                    command.CommandText = "SELECT 1 as ColInteger;  SELECT 'STRING' as ColString";
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        HashSet<string> columnNames = new HashSet<string>();
                        do
                        {
                            DataTable schemaTable = reader.GetSchemaTable();
                            foreach (DataRow myField in schemaTable.Rows)
                            {
                                columnNames.Add(myField["ColumnName"].ToString());
                            }

                        } while (reader.NextResult());

                        Assert.Contains("ColInteger", columnNames);
                        Assert.Contains("ColString", columnNames);
                    }
                }
            }
        }

        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public static void GetSchemaTable_returns_null_when_no_resultset()
        {
            using (SqlConnection connection = new SqlConnection(DataTestUtility.TCPConnectionString))
            {
                connection.Open();

                using (SqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1";
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        reader.NextResult();
                        Assert.NotNull(reader.GetSchemaTable());
                    }
                }
            }
        }
        
        // Checks for the IsColumnSet bit in the GetSchemaTable for Sparse columns
        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public static void CheckSparseColumnBit()
        {
            const int sparseColumns = 4095;
            string tempTableName = DataTestUtility.GenerateObjectName();

            // TSQL for "CREATE TABLE" with sparse columns
            // table name will be provided as an argument
            StringBuilder createBuilder = new StringBuilder("CREATE TABLE {0} ([ID] int PRIMARY KEY, [CSET] xml COLUMN_SET FOR ALL_SPARSE_COLUMNS NULL");

            // TSQL to create the same table, but without the column set column and without sparse
            // also, it has only 1024 columns, which is the server limit in this case
            StringBuilder createNonSparseBuilder = new StringBuilder("CREATE TABLE {0} ([ID] int PRIMARY KEY");

            // TSQL to select all columns from the sparse table, without columnset one
            StringBuilder selectBuilder = new StringBuilder("SELECT [ID]");

            // TSQL to select all columns from the sparse table, with a limit of 1024 (for bulk-copy test)
            StringBuilder selectNonSparseBuilder = new StringBuilder("SELECT [ID]");

            // add sparse columns
            for (int c = 0; c < sparseColumns; c++)
            {
                createBuilder.AppendFormat(", [C{0}] int SPARSE NULL", c);
                selectBuilder.AppendFormat(", [C{0}]", c);
            }

            createBuilder.Append(")");
            // table name provided as an argument
            selectBuilder.Append(" FROM {0}");

            string selectStatementFormat = selectBuilder.ToString();
            string createStatementFormat = createBuilder.ToString();

            // add a row with nulls only
            using (SqlConnection con = new SqlConnection(DataTestUtility.TCPConnectionString))
            using (SqlCommand cmd = con.CreateCommand())
            {
                try
                {
                    con.Open();

                    cmd.CommandType = CommandType.Text;
                    cmd.CommandText = string.Format(createStatementFormat, tempTableName);
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = string.Format("INSERT INTO {0} ([ID]) VALUES (0)", tempTableName);// insert row with values set to their defaults (DBNULL)
                    cmd.ExecuteNonQuery();

                    // run the test cases
                    Assert.True(IsColumnBitSet(con, string.Format("SELECT [ID], [CSET], [C1] FROM {0}", tempTableName), indexOfColumnSet: 1));
                }
                finally
                {
                    // drop the temp table to release its resources
                    cmd.CommandText = "DROP TABLE " + tempTableName;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup), nameof(DataTestUtility.IsNotAzureServer))]
        public static void CollatedDataReaderTest()
        {
            var databaseName = DataTestUtility.GetUniqueName("DB");
            // Remove square brackets
            var dbName = databaseName.Substring(1, databaseName.Length - 2);

            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(DataTestUtility.TCPConnectionString)
            {
                InitialCatalog = dbName,
                Pooling = false
            };

            using (SqlConnection con = new SqlConnection(DataTestUtility.TCPConnectionString))
            using (SqlCommand cmd = con.CreateCommand())
            {
                try
                {
                    con.Open();

                    // Create collated database
                    cmd.CommandText = $"CREATE DATABASE {databaseName} COLLATE KAZAKH_90_CI_AI";
                    cmd.ExecuteNonQuery();

                    //Create connection without pooling in order to delete database later.
                    using (SqlConnection dbCon = new SqlConnection(builder.ConnectionString))
                    using (SqlCommand dbCmd = dbCon.CreateCommand())
                    {
                        var data = "TestData";

                        dbCon.Open();
                        dbCmd.CommandText = $"SELECT '{data}'";
                        using (SqlDataReader reader = dbCmd.ExecuteReader())
                        {
                            reader.Read();
                            Assert.Equal(data, reader.GetString(0));
                        }
                    }

                    // Let connection close safely before dropping database for slow servers.
                    Thread.Sleep(500);
                }
                catch (SqlException e)
                {
                    Assert.True(false, $"Unexpected Exception occurred: {e.Message}");
                }
                finally
                {
                    cmd.CommandText = $"DROP DATABASE {databaseName}";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static bool IsColumnBitSet(SqlConnection con, string selectQuery, int indexOfColumnSet)
        {
            bool columnSetPresent = false;
            {
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandText = selectQuery;
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        DataTable schemaTable = reader.GetSchemaTable();

                        for (int i = 0; i < schemaTable.Rows.Count; i++)
                        {
                            bool isColumnSet = (bool)schemaTable.Rows[i]["IsColumnSet"];

                            if (indexOfColumnSet == i)
                            {
                                columnSetPresent = true;
                            }
                        }
                    }
                }
            }
            return columnSetPresent;
        }
    }
}
