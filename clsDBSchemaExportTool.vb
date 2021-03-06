﻿Option Strict On

' -------------------------------------------------------------------------------
' Written by Matthew Monroe for the Department of Energy (PNNL, Richland, WA) in 2014
'
' E-mail: matthew.monroe@pnnl.gov or proteomics@pnnl.gov
' Website: https://omics.pnl.gov/ or https://www.pnnl.gov/sysbio/ or https://panomics.pnnl.gov/
' -------------------------------------------------------------------------------
'
' Licensed under the 2-Clause BSD License; you may not use this file except
' in compliance with the License.  You may obtain a copy of the License at
' https://opensource.org/licenses/BSD-2-Clause
'
' Copyright 2018 Battelle Memorial Institute

Imports System.IO
Imports System.Text.RegularExpressions
Imports PRISM

''' <summary>
''' This class exports the schema for one or more databases on a server
''' </summary>
Public Class clsDBSchemaExportTool
    Inherits FileProcessor.ProcessDirectoriesBase

    ''' <summary>
    ''' Constructor
    ''' </summary>
    Public Sub New()
        MyBase.mFileDate = "October 22, 2018"
        mDateMatcher = New Regex("'\d+/\d+/\d+ \d+:\d+:\d+ [AP]M'", RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        InitializeLocalVariables()
    End Sub

#Region "Constants and Enums"

    ' Error codes specialized for this class
    Private Enum eDBSchemaExportTool As Integer
        NoError = 0
        UnspecifiedError = -1
    End Enum

    Private Enum eDifferenceReasonType
        Unchanged = 0
        NewFile = 1
        Changed = 2
    End Enum

    Private Enum eRepoManagerType
        Svn = 0
        Hg = 1
        Git = 2
    End Enum

#End Region

#Region "Structures"

#End Region

#Region "Classwide Variables"

    Private mServer As String
    Private mDatabase As String

    Private mSchemaExportOptions As clsSchemaExportOptions

    Private mSubtaskDescription As String
    Private mSubtaskPercentComplete As Single

    Public Event SubtaskProgressChanged(taskDescription As String, percentComplete As Single)      ' PercentComplete ranges from 0 to 100, but can contain decimal percentage values

    Private mLocalErrorCode As eDBSchemaExportTool

    Private WithEvents mDBSchemaExporter As clsExportDBSchema

    Private ReadOnly mDateMatcher As Regex

#End Region

#Region "Properties"

    Public Property AutoSelectTableDataToExport As Boolean

    Public Property TableDataToExportFile As String

    Public Property CreateDirectoryForEachDB As Boolean

    Public Property DatabaseSubdirectoryPrefix As String

    Public Property PreviewExport As Boolean

    Public Property ShowStats As Boolean

    Public Property Sync As Boolean

    Public Property SyncDirectoryPath As String

    Public Property GitUpdate As Boolean

    Public Property HgUpdate As Boolean

    Public Property SvnUpdate As Boolean

    Public Property CommitUpdates As Boolean


#End Region

    Private Function CheckPlural(value As Integer, textIfOne As String, textIfSeveral As String) As String
        If value = 1 Then
            Return textIfOne
        Else
            Return textIfSeveral
        End If
    End Function

    ''' <summary>
    ''' Export database schema to the specified directory
    ''' </summary>
    ''' <param name="outputDirectoryPath">Output directory path</param>
    ''' <param name="serverName">Server name</param>
    ''' <param name="dctDatabaseNamesAndOutputPaths">Dictionary where keys are database names and values will be updated to have the output directory path used</param>
    ''' <returns>True if success, false if a problem</returns>
    ''' <remarks>
    ''' If CreatedDirectoryForEachDB is true, or if dctDatabaseNamesAndOutputPaths contains more than one entry,
    ''' then each database will be scripted to a subdirectory below the output directory
    ''' </remarks>
    Public Function ExportSchema(
      outputDirectoryPath As String,
      serverName As String,
      ByRef dctDatabaseNamesAndOutputPaths As Dictionary(Of String, String)) As Boolean

        Return ExportSchema(outputDirectoryPath, serverName, dctDatabaseNamesAndOutputPaths, True, "", "")

    End Function

    ''' <summary>
    ''' Export database schema to the specified directory
    ''' </summary>
    ''' <param name="outputDirectoryPath">Output directory path</param>
    ''' <param name="serverName">Server name</param>
    ''' <param name="dctDatabaseNamesAndOutputPaths">Dictionary where keys are database names and values will be updated to have the output directory path used</param>
    ''' <param name="useIntegratedAuthentication">True for integrated authentication, false to use loginUsername and loginPassword</param>
    ''' <param name="loginUsername">Sql server login to use when useIntegratedAuthentication is false</param>
    ''' <param name="loginPassword">Sql server password to use when useIntegratedAuthentication is false</param>
    ''' <returns>True if success, false if a problem</returns>
    ''' <remarks>
    ''' If CreateDirectoryForEachDB is true, or if dctDatabaseNamesAndOutputPaths contains more than one entry,
    ''' then each database will be scripted to a subdirectory below the output directory
    ''' </remarks>
    Public Function ExportSchema(
      outputDirectoryPath As String,
      serverName As String,
      ByRef dctDatabaseNamesAndOutputPaths As Dictionary(Of String, String),
      useIntegratedAuthentication As Boolean,
      loginUsername As String,
      loginPassword As String) As Boolean

        Try
            If String.IsNullOrWhiteSpace(outputDirectoryPath) Then
                Throw New ArgumentException("Output directory cannot be empty", NameOf(outputDirectoryPath))
            End If

            If Not Directory.Exists(outputDirectoryPath) Then
                ' Try to create the missing directory
                ShowMessage("Creating " & outputDirectoryPath)
                Directory.CreateDirectory(outputDirectoryPath)
            End If

            With mSchemaExportOptions
                .OutputDirectoryPath = outputDirectoryPath

                .OutputDirectoryNamePrefix = Me.DatabaseSubdirectoryPrefix
                .CreateDirectoryForEachDB = Me.CreateDirectoryForEachDB

                If dctDatabaseNamesAndOutputPaths.Count > 0 Then
                    .CreateDirectoryForEachDB = True
                End If

                .AutoSelectTableNamesForDataExport = Me.AutoSelectTableDataToExport

                ' Note: mDBSchemaExporter & mTableNameAutoSelectRegEx will be passed to mDBSchemaExporter below

                With .ConnectionInfo
                    .ServerName = serverName
                    .UseIntegratedAuthentication = useIntegratedAuthentication
                    .UserName = loginUsername
                    .Password = loginPassword
                End With

            End With

        Catch ex As Exception
            HandleException("Error in ExportSchema configuring the options", ex)
            Exit Function
        End Try

        Try
            Dim dtStartTime = DateTime.UtcNow

            If mDBSchemaExporter Is Nothing Then
                mDBSchemaExporter = New clsExportDBSchema
                RegisterEvents(mDBSchemaExporter)
            End If

            mDBSchemaExporter.TableNamesToAutoSelect = GetTableNamesToAutoExportData()
            mDBSchemaExporter.TableNameAutoSelectRegEx = GetTableRegExToAutoExportData()

            If Not AutoSelectTableDataToExport Then
                mDBSchemaExporter.TableNamesToAutoSelect.Clear()
                mDBSchemaExporter.TableNameAutoSelectRegEx.Clear()
            End If

            Dim lstDatabaseList = dctDatabaseNamesAndOutputPaths.Keys().ToList()
            Dim lstTableNamesForDataExport = New List(Of String)

            If Not String.IsNullOrWhiteSpace(Me.TableDataToExportFile) Then
                lstTableNamesForDataExport = LoadTableNamesForDataExport(Me.TableDataToExportFile)
            End If

            mDBSchemaExporter.ShowStats = Me.ShowStats
            mDBSchemaExporter.PreviewExport = Me.PreviewExport

            Dim success = mDBSchemaExporter.ScriptServerAndDBObjects(mSchemaExportOptions, lstDatabaseList, lstTableNamesForDataExport)

            ' Populate a dictionary with the database names (properly capitalized) and the output directory path used for each
            Dim databaseNameToDirectoryMap = New Dictionary(Of String, String)(StringComparer.CurrentCultureIgnoreCase)

            For Each exportedDatabase In mDBSchemaExporter.SchemaOutputDirectories
                databaseNameToDirectoryMap.Add(exportedDatabase.Key, exportedDatabase.Value)
            Next

            ' Add any other databases in lstDatabaseList that are missing (as would be the case if it doesn't exist on the server)
            For Each databaseName In lstDatabaseList
                If Not databaseNameToDirectoryMap.ContainsKey(databaseName) Then
                    databaseNameToDirectoryMap.Add(databaseName, String.Empty)
                End If
            Next

            ' Now update dctDatabaseNamesAndOutputPaths to match databaseNameToDirectoryMap (which has properly capitalized database names)
            dctDatabaseNamesAndOutputPaths = databaseNameToDirectoryMap

            If Me.ShowStats Then
                Console.WriteLine("Exported database schema in " & DateTime.UtcNow.Subtract(dtStartTime).TotalSeconds.ToString("0.0") & " seconds")
            End If

            Return success

        Catch ex As Exception
            HandleException("Error in ExportSchema configuring mDBSchemaExporter", ex)
            Return False
        End Try

    End Function

    ''' <summary>
    ''' Compare the contents of the two files using a line-by-line comparison
    ''' </summary>
    ''' <param name="fiBase">Base file</param>
    ''' <param name="fiComparison">Comparison file</param>
    ''' <param name="eDifferenceReason">Output parameter: reason for the difference, or eDifferenceReasonType.Unchanged if identical</param>
    ''' <returns>True if the files differ (i.e. if they do not match)</returns>
    ''' <remarks>
    ''' Several files are treated specially to ignore changing dates or numbers, in particular:
    ''' In DBDefinition files, the database size values are ignored
    ''' In T_Process_Step_Control_Data files, in the Insert Into lines, any date values or text after a date value is ignored
    ''' In T_Signatures_Data files, in the Insert Into lines, any date values are ignored
    ''' </remarks>
    Private Function FilesDiffer(
      fiBase As FileInfo,
      fiComparison As FileInfo,
      ByRef eDifferenceReason As eDifferenceReasonType) As Boolean

        Try
            eDifferenceReason = eDifferenceReasonType.Unchanged

            If Not fiBase.Exists Then Return False

            If Not fiComparison.Exists Then
                eDifferenceReason = eDifferenceReasonType.NewFile
                Return True
            End If

            Dim dbDefinitionFile = False

            Dim lstDateIgnoreFiles = New SortedSet(Of String)(StringComparer.CurrentCultureIgnoreCase) From {
                "T_Process_Step_Control_Data.sql",
                "T_Signatures_Data.sql",
                "T_MTS_Peptide_DBs_Data.sql",
                "T_MTS_MT_DBs_Data.sql",
                "T_Processor_Tool_Data.sql",
                "T_Processor_Tool_Group_Details_Data.sql"
            }

            Dim ignoreInsertIntoDates = False

            If fiBase.Name.StartsWith(clsExportDBSchema.DB_DEFINITION_FILE_PREFIX) Then
                ' DB Definition file; don't worry if file lengths differ
                dbDefinitionFile = True
            ElseIf lstDateIgnoreFiles.Contains(fiBase.Name) Then
                ' Files where date values are being ignored; don't worry if file lengths differ
                ShowMessage("Ignoring date values in file " & fiBase.Name)
                ignoreInsertIntoDates = True
            Else
                If fiBase.Length <> fiComparison.Length Then
                    eDifferenceReason = eDifferenceReasonType.Changed
                    Return True
                End If
            End If

            ' Perform a line-by-line comparison
            Using srBaseFile = New StreamReader(New FileStream(fiBase.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))

                Using srComparisonFile = New StreamReader(New FileStream(fiComparison.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))

                    While Not srBaseFile.EndOfStream
                        Dim dataLine = srBaseFile.ReadLine()

                        If Not srComparisonFile.EndOfStream Then
                            Dim comparisonLine = srComparisonFile.ReadLine()

                            Dim linesMatch = StringMatch(dataLine, comparisonLine)

                            If linesMatch Then Continue While

                            If dbDefinitionFile AndAlso dataLine.StartsWith("( NAME =") AndAlso comparisonLine.StartsWith("( NAME =") Then
                                ' DBDefinition file
                                Dim lstSourceCols = dataLine.Split(","c).ToList()
                                Dim lstComparisonCols = comparisonLine.Split(","c).ToList()

                                If lstSourceCols.Count = lstComparisonCols.Count Then
                                    linesMatch = True
                                    For dataColumnIndex = 0 To lstSourceCols.Count - 1
                                        Dim sourceValue = lstSourceCols(dataColumnIndex).Trim()
                                        Dim comparisonValue = lstComparisonCols(dataColumnIndex).Trim()

                                        If sourceValue.StartsWith("SIZE") AndAlso comparisonValue.StartsWith("SIZE") Then
                                            ' Don't worry if these values differ
                                        ElseIf Not StringMatch(sourceValue, comparisonValue) Then
                                            linesMatch = False
                                            Exit For
                                        End If
                                    Next
                                End If

                            End If

                            If ignoreInsertIntoDates AndAlso dataLine.StartsWith("INSERT INTO ") AndAlso comparisonLine.StartsWith("INSERT INTO ") Then
                                ' Data file where we're ignoring dates
                                ' Truncate each of the data lines at the first occurrence of a date
                                Dim matchBaseFile = mDateMatcher.Match(dataLine)
                                Dim matchComparisonFile = mDateMatcher.Match(comparisonLine)

                                If matchBaseFile.Success AndAlso matchComparisonFile.Success Then
                                    dataLine = dataLine.Substring(0, matchBaseFile.Index)
                                    comparisonLine = comparisonLine.Substring(0, matchComparisonFile.Index)
                                    linesMatch = StringMatch(dataLine, comparisonLine)
                                End If
                            End If

                            If Not linesMatch Then
                                ' Difference found
                                eDifferenceReason = eDifferenceReasonType.Changed
                                Return True
                            End If

                        End If

                    End While
                End Using
            End Using

            Return False

        Catch ex As Exception
            HandleException("Error in FilesDiffer", ex)
            Return True
        End Try

    End Function

    Public Overrides Function GetErrorMessage() As String
        ' Returns "" if no error

        Dim strErrorMessage As String

        If MyBase.ErrorCode = ProcessDirectoriesErrorCodes.LocalizedError OrElse
           MyBase.ErrorCode = ProcessDirectoriesErrorCodes.NoError Then
            Select Case mLocalErrorCode
                Case eDBSchemaExportTool.NoError
                    strErrorMessage = ""

                Case eDBSchemaExportTool.UnspecifiedError
                    strErrorMessage = "Unspecified localized error"

                Case Else
                    ' This shouldn't happen
                    strErrorMessage = "Unknown error state"
            End Select
        Else
            strErrorMessage = MyBase.GetBaseClassErrorMessage()
        End If

        Return strErrorMessage
    End Function

    Public Shared Function GetTableNamesToAutoExportData() As List(Of String)

        Dim lstTableNames = New List(Of String) From {
            "T_Folder_Paths",                       ' MT_Main
            "T_Peak_Matching_Defaults",             ' MT DBs
            "T_Process_Config",
            "T_Process_Config_Parameters",
            "T_Quantitation_Defaults",
            "T_MTS_DB_Types",                       ' MTS_Master
            "T_MTS_MT_DBs",
            "T_MTS_Peptide_DBs",
            "T_MTS_Servers",
            "T_MyEMSL_Cache_Paths",
            "T_Dataset_Scan_Type_Name",             ' Peptide DB
            "T_Match_Methods",                      ' Prism_IFC
            "T_SP_Categories",
            "T_SP_Column_Direction_Types",
            "T_SP_Glossary",
            "T_SP_List",
            "T_Analysis_Job_Processor_Tools",       ' Prism_RPT
            "T_Analysis_Job_Processors",
            "T_Status",
            "T_Dataset_Rating_Name",                ' DMS5
            "T_Default_PSM_Job_Types",
            "T_Enzymes",
            "T_Instrument_Ops_Role",
            "T_MiscPaths",
            "T_Modification_Types",
            "T_MyEMSLState",
            "T_Predefined_Analysis_Scheduling_Rules",
            "T_Research_Team_Roles",
            "T_Residues",
            "T_User_Operations",
            "T_Properties",                          ' Data_Package
            "T_URI_Paths",
            "T_Event_Target",                        ' Manager Control
            "T_Mgrs",
            "T_MgrState",
            "T_MgrType_ParamType_Map",
            "T_MgrTypes",
            "T_ParamType",
            "ontology",                              ' Ontology_Lookup
            "T_Unimod_AminoAcids",
            "T_Unimod_Bricks",
            "T_Unimod_Specificity_NL",
            "T_Automatic_Jobs",                      ' DMS_Pipeline and DMS_Capture
            "T_Default_SP_Params",
            "T_Processor_Instrument",
            "T_Processor_Tool",
            "T_Processor_Tool_Group_Details",
            "T_Processor_Tool_Groups",
            "T_Scripts",
            "T_Scripts_History",
            "T_Signatures",
            "T_Step_Tools",
            "T_Annotation_Types",                    ' Protein Sequences
            "T_Archived_File_Types",
            "T_Creation_Option_Keywords",
            "T_Creation_Option_Values",
            "T_Naming_Authorities",
            "T_Output_Sequence_Types",
            "T_Protein_Collection_Types",
            "AlertContacts",                         ' dba
            "AlertSettings"
        }

        Return lstTableNames

    End Function


    Public Shared Function GetTableRegExToAutoExportData() As List(Of String)
        Dim lstRegExSpecs = New List(Of String) From {
            ".*_?Type_?Name",
            ".*_?State_?Name",
            ".*_State",
            ".*_States"
        }

        Return lstRegExSpecs
    End Function

    Private Sub InitializeLocalVariables()

        MyBase.ReThrowEvents = False
        MyBase.LogMessagesToFile = False

        mLocalErrorCode = eDBSchemaExportTool.NoError

        mServer = String.Empty
        mDatabase = String.Empty

        'mUseIntegratedAuthentication = True
        'mUsername = String.Empty
        'mPassword = String.Empty

        mSchemaExportOptions = clsExportDBSchema.GetDefaultSchemaExportOptions()

        mSubtaskDescription = String.Empty
        mSubtaskPercentComplete = 0

        Me.AutoSelectTableDataToExport = True

        Me.TableDataToExportFile = String.Empty

        Me.CreateDirectoryForEachDB = True

        Me.DatabaseSubdirectoryPrefix = clsExportDBSchema.DEFAULT_DB_OUTPUT_DIRECTORY_NAME_PREFIX

        Me.PreviewExport = False
        Me.ShowStats = False
        Me.Sync = False

        Me.SyncDirectoryPath = String.Empty

        Me.GitUpdate = False
        Me.HgUpdate = False
        Me.SvnUpdate = False

        Me.CommitUpdates = False

    End Sub

    Private Function LoadParameterFileSettings(strParameterFilePath As String) As Boolean

        Const OPTIONS_SECTION = "DBSchemaExportTool"

        Dim objSettingsFile As New XmlSettingsFileAccessor

        Try

            If strParameterFilePath Is Nothing OrElse strParameterFilePath.Length = 0 Then
                ' No parameter file specified; nothing to load
                Return True
            End If

            If Not File.Exists(strParameterFilePath) Then
                ' See if strParameterFilePath points to a file in the same directory as the application
                strParameterFilePath = Path.Combine(Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location), Path.GetFileName(strParameterFilePath))
                If Not File.Exists(strParameterFilePath) Then
                    MyBase.SetBaseClassErrorCode(ProcessDirectoriesErrorCodes.ParameterFileNotFound)
                    Return False
                End If
            End If

            If objSettingsFile.LoadSettings(strParameterFilePath) Then
                If Not objSettingsFile.SectionPresent(OPTIONS_SECTION) Then
                    ShowErrorMessage("The node '<section name=""" & OPTIONS_SECTION & """> was not found in the parameter file: " & strParameterFilePath)
                    MyBase.SetBaseClassErrorCode(ProcessDirectoriesErrorCodes.InvalidParameterFile)
                    Return False
                Else
                    If objSettingsFile.GetParam(OPTIONS_SECTION, "LogMessages", False) Then
                        MyBase.LogMessagesToFile = True
                    End If

                    Dim value = objSettingsFile.GetParam(OPTIONS_SECTION, "LogDirectory", String.Empty)
                    If Not String.IsNullOrEmpty(value) Then
                        MyBase.LogDirectoryPath = value
                    End If

                End If
            End If

        Catch ex As Exception
            HandleException("Error in LoadParameterFileSettings", ex)
            Return False
        End Try

        Return True

    End Function

    Private Function LoadTableNamesForDataExport(tableDataFilePath As String) As List(Of String)

        Dim lstTableNames = New SortedSet(Of String)

        Try
            If String.IsNullOrWhiteSpace(tableDataFilePath) Then
                Return lstTableNames.ToList()
            End If

            Dim fiDatafile = New FileInfo(tableDataFilePath)

            If Not fiDatafile.Exists Then
                Console.WriteLine()
                ShowMessage("Table Data File not found; default tables will be used")
                ShowErrorMessage("File not found: " & fiDatafile.FullName)
                Return lstTableNames.ToList()
            End If

            Using srReader = New StreamReader(New FileStream(fiDatafile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                While Not srReader.EndOfStream
                    Dim dataLine = srReader.ReadLine()

                    If Not String.IsNullOrWhiteSpace(dataLine) Then
                        If Not lstTableNames.Contains(dataLine) Then
                            lstTableNames.Add(dataLine)
                        End If
                    End If

                End While
            End Using


        Catch ex As Exception
            HandleException("Error in LoadTableNamesForDataExport", ex)
        End Try

        Return lstTableNames.ToList()

    End Function

    Private Function ParseGitStatus(
     diTargetDirectory As FileSystemInfo,
     standardOutput As String,
     ByRef intModifiedFileCount As Integer) As Boolean

        ' Example output for Git with verbose output
        ' 	# On branch master
        ' 	# Your branch is behind 'origin/master' by 1 commit, and can be fast-forwarded.
        ' 	#   (use "git pull" to update your local branch)
        ' 	#
        '	# Changes not staged for commit:
        '	#   (use "git add <file>..." to update what will be committed)
        '	#   (use "git checkout -- <file>..." to discard changes in working directory)
        '	#
        '	#       modified:   PNNLOmics/Algorithms/Alignment/LcmsWarp/LcmsWarp.cs
        '	#
        '	# Untracked files:
        '	#   (use "git add <file>..." to include in what will be committed)
        '	#
        '	#       MyNewFile.txt
        '	no changes added to commit (use "git add" and/or "git commit -a")

        ' Example output with Git for short output (-s)
        '  M PNNLOmics/Algorithms/Alignment/LcmsWarp/LcmsWarp.cs
        ' ?? MyNewFile.txt

        Dim lstNewOrModifiedStatusSymbols = New List(Of Char) From {"M"c, "A"c, "R"c}

        Using srReader = New StringReader(standardOutput)
            While srReader.Peek > -1
                Dim statusLine = srReader.ReadLine()

                If String.IsNullOrWhiteSpace(statusLine) OrElse statusLine.Length < 4 Then Continue While

                If statusLine.StartsWith("fatal: Not a git repository") Then
                    ShowErrorMessage("Directory is not tracked by Git: " & diTargetDirectory.FullName)
                    Return False
                End If

                Dim fileIndexStatus As Char = statusLine.Chars(0)
                Dim fileWorkTreeStatus As Char = statusLine.Chars(1)
                ' Dim filePath = statusLine.Substring(3)

                If fileIndexStatus = "?"c Then
                    ' New file; added by the calling function
                ElseIf lstNewOrModifiedStatusSymbols.Contains(fileIndexStatus) OrElse lstNewOrModifiedStatusSymbols.Contains(fileWorkTreeStatus) Then
                    intModifiedFileCount += 1
                End If

            End While
        End Using

        Return True

    End Function

    Private Function ParseSvnHgStatus(
      diTargetDirectory As FileSystemInfo,
      standardOutput As String,
      eRepoManager As eRepoManagerType,
      ByRef intModifiedFileCount As Integer) As Boolean

        ' Example output for Svn where M is modified, ? is new, and ! means deleted
        '	    M       F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\UpdateAnalysisJobStateNameCached.sql
        '	    ?       F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\UpdateAnalysisJobToolNameCached.sql
        '	    M       F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\V_Analysis_Job_List_Report_2.sql
        '	    M       F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\V_GetPipelineJobParameters.sql
        '	    !       F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\x_V_Analysis_Job.sql

        ' Example output for Hg where M is modified, ? is new, and ! means deleted
        '	    M F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\UpdateAnalysisJobStateNameCached.sql
        '	    ? F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\UpdateAnalysisJobToolNameCached.sql
        '	    M F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\V_Analysis_Job_List_Report_2.sql
        '	    M F:\Documents\Projects\DataMining\Database_Schema\DMS\DMS5\V_GetPipelineJobParameters.sql


        Dim lstNewOrModifiedStatusSymbols = New List(Of Char) From {"M"c, "A"c, "R"c}
        Dim minimumLineLength As Integer

        If eRepoManager = eRepoManagerType.Svn Then
            minimumLineLength = 8
        Else
            minimumLineLength = 3
        End If

        Using srReader = New StringReader(standardOutput)
            While srReader.Peek > -1
                Dim statusLine = srReader.ReadLine()

                If String.IsNullOrWhiteSpace(statusLine) OrElse statusLine.Length < minimumLineLength Then Continue While

                If statusLine.StartsWith("svn: warning") AndAlso statusLine.Contains("is not a working copy") Then
                    ShowErrorMessage("Directory is not tracked by SVN: " & diTargetDirectory.FullName)
                    Return False
                ElseIf statusLine.StartsWith("abort: no repository found in ") Then
                    ShowErrorMessage("Directory is not tracked by Hg: " & diTargetDirectory.FullName)
                    Return False
                End If

                Dim fileModStatus As Char = statusLine.Chars(0)
                Dim filePropertyStatus = " "c

                If eRepoManager = eRepoManagerType.Svn Then
                    filePropertyStatus = statusLine.Chars(1)
                    ' Dim filePath = statusLine.Substring(8).Trim()
                Else
                    ' Dim filePath = statusLine.Substring(2).Trim()
                End If

                If lstNewOrModifiedStatusSymbols.Contains(fileModStatus) Then
                    intModifiedFileCount += 1

                ElseIf filePropertyStatus = "M"c Then
                    intModifiedFileCount += 1

                ElseIf fileModStatus = "?"c Then
                    ' New file; added by the calling function
                End If

            End While
        End Using

        Return True

    End Function

    Public Overloads Overrides Function ProcessDirectory(strInputDirectoryPath As String,
      strOutputDirectoryAlternatePath As String,
      strParameterFilePath As String,
      blnResetErrorCode As Boolean) As Boolean
        ' Returns True if success, False if failure

        ' Assume the input directory points to the target directory where the database schema files will be created

        Return ProcessDatabase(strInputDirectoryPath, mServer, mDatabase)

    End Function

    Public Shadows Function ProcessAndRecurseDirectories(strInputDirectoryPath As String) As Boolean
        Return ProcessAndRecurseDirectories(strInputDirectoryPath, String.Empty)
    End Function

    Public Shadows Function ProcessAndRecurseDirectories(strInputDirectoryPath As String, intMaxLevelsToRecurse As Integer) As Boolean
        Return ProcessAndRecurseDirectories(strInputDirectoryPath, String.Empty, String.Empty, intMaxLevelsToRecurse)
    End Function

    Public Shadows Function ProcessAndRecurseDirectories(strInputDirectoryPath As String, strOutputDirectoryAlternatePath As String) As Boolean
        Return ProcessAndRecurseDirectories(strInputDirectoryPath, strOutputDirectoryAlternatePath, String.Empty)
    End Function

    Public Shadows Function ProcessAndRecurseDirectories(strInputDirectoryPath As String, strOutputDirectoryAlternatePath As String, strParameterFilePath As String) As Boolean
        Return ProcessAndRecurseDirectories(strInputDirectoryPath, strOutputDirectoryAlternatePath, strParameterFilePath, 0)
    End Function

    Public Shadows Function ProcessAndRecurseDirectories(
      strInputDirectoryPath As String,
      strOutputDirectoryAlternatePath As String,
      strParameterFilePath As String,
      intMaxLevelsToRecurse As Integer) As Boolean
        ' Returns True if success, False if failure

        Return ProcessDirectory(strInputDirectoryPath, strOutputDirectoryAlternatePath, strParameterFilePath, True)

    End Function

    ''' <summary>
    ''' Script the objects for one or more databases
    ''' </summary>
    ''' <param name="outputDirectoryPath">Output directory path</param>
    ''' <param name="serverName">Server name</param>
    ''' <param name="databaseList">Database names to script</param>
    ''' <returns>True if success, false if a problem</returns>
    Public Function ProcessDatabase(outputDirectoryPath As String, serverName As String, databaseList As IEnumerable(Of String)) As Boolean
        Dim success As Boolean

        If String.IsNullOrWhiteSpace(outputDirectoryPath) Then
            Throw New ArgumentException("Output directory path must be defined", NameOf(outputDirectoryPath))
        End If

        If String.IsNullOrWhiteSpace(serverName) Then
            Throw New ArgumentException("Server name must be defined", NameOf(serverName))
        End If

        If databaseList.Count = 0 Then
            Throw New ArgumentException("Database list cannot be empty", NameOf(databaseList))
        End If

        Try
            ' Keys in this dictionary are database names
            ' Values are the output directory path used (values will be defined by ExportSchema then used by SyncSchemaFiles)

            Dim dctDatabaseNamesAndOutputPaths = New Dictionary(Of String, String)
            For Each databaseName In databaseList
                dctDatabaseNamesAndOutputPaths.Add(databaseName, String.Empty)
            Next

            success = ExportSchema(outputDirectoryPath, serverName, dctDatabaseNamesAndOutputPaths)

            If success AndAlso Sync Then
                success = SyncSchemaFiles(dctDatabaseNamesAndOutputPaths, SyncDirectoryPath)
            End If

            Return success

        Catch ex As Exception
            HandleException("Error in ProcessDatabase", ex)
            Return False
        End Try

    End Function

    ''' <summary>
    ''' Script the objects for one database
    ''' </summary>
    ''' <param name="outputDirectoryPath">Output directory path</param>
    ''' <param name="serverName">Server name</param>
    ''' <param name="databaseName">Database name to script</param>
    ''' <returns>True if success, false if a problem</returns>
    Public Function ProcessDatabase(outputDirectoryPath As String, serverName As String, databaseName As String) As Boolean

        Dim databaseList As New List(Of String) From {databaseName}

        Return ProcessDatabase(outputDirectoryPath, serverName, databaseList)

    End Function

    Private Function RunCommand(
      exePath As String,
      cmdArgs As String,
      workDirPath As String,
      ByRef standardOutput As String,
      ByRef errorOutput As String,
      maxRuntimeSeconds As Integer) As Boolean

        standardOutput = String.Empty
        errorOutput = String.Empty

        Try

            Dim programRunner = New ProgRunner
            With programRunner
                .Arguments = cmdArgs
                .CreateNoWindow = True
                .MonitoringInterval = 100
                .Name = Path.GetFileNameWithoutExtension(exePath)
                .Program = exePath
                .Repeat = False
                .RepeatHoldOffTime = 0
                .WorkDir = workDirPath
                .CacheStandardOutput = True
                .EchoOutputToConsole = True
                .WriteConsoleOutputToFile = False
                .ConsoleOutputFilePath = String.Empty
            End With
            RegisterEvents(programRunner)

            Dim dtStartTime = DateTime.UtcNow
            Dim dtLastStatus = DateTime.UtcNow
            Dim executionAborted = False

            ' Start the program executing
            programRunner.StartAndMonitorProgram()

            ' Wait for it to exit
            If maxRuntimeSeconds < 10 Then maxRuntimeSeconds = 10

            ' Loop until program is complete, or until maxRuntimeSeconds seconds elapses
            While (programRunner.State <> ProgRunner.States.NotMonitoring)
                Threading.Thread.Sleep(100)

                Dim elapsedSeconds = DateTime.UtcNow.Subtract(dtStartTime).TotalSeconds

                If elapsedSeconds > maxRuntimeSeconds Then
                    ShowErrorMessage("Program execution has surpassed " & maxRuntimeSeconds & " seconds; aborting " & exePath)
                    programRunner.StopMonitoringProgram(kill:=True)
                    executionAborted = True
                End If

                If DateTime.UtcNow.Subtract(dtLastStatus).TotalSeconds > 15 Then
                    dtLastStatus = DateTime.UtcNow
                    Console.WriteLine("Waiting for " & Path.GetFileName(exePath) & ", " & elapsedSeconds.ToString("0") & " seconds elapsed")
                End If

            End While

            If executionAborted Then
                Console.WriteLine("ProgramRunner was aborted for " & exePath)
                Return True
            Else
                standardOutput = programRunner.CachedConsoleOutput
                errorOutput = programRunner.CachedConsoleError
                Return True
            End If

        Catch ex As Exception
            HandleException("Error in RunCommand", ex)
            Return False
        End Try

    End Function

    ''' <summary>
    ''' Compare two strings
    ''' </summary>
    ''' <param name="text1"></param>
    ''' <param name="text2"></param>
    ''' <returns>True if the strings match, otherwise false</returns>
    ''' <remarks>Case sensitive comparison</remarks>
    Private Shared Function StringMatch(text1 As String, text2 As String) As Boolean

        If String.Compare(text1, text2) = 0 Then
            Return True
        Else
            Return False
        End If

    End Function

    Private Function SyncSchemaFiles(lstDatabaseNamesAndOutputPaths As ICollection(Of KeyValuePair(Of String, String)), directoryPathForSync As String) As Boolean
        Try
            Dim dtStartTime = DateTime.UtcNow

            ResetProgress("Synchronizing with " & directoryPathForSync)

            Dim intDBsProcessed = 0
            Dim includeDbNameInCommitMessage = (lstDatabaseNamesAndOutputPaths.Count > 1)

            For Each dbEntry In lstDatabaseNamesAndOutputPaths

                Dim databaseName = dbEntry.Key
                Dim schemaOutputDirectory = dbEntry.Value

                If String.IsNullOrWhiteSpace(schemaOutputDirectory) Then
                    ShowErrorMessage("Schema output directory was not reported for " & databaseName & "; unable to synchronize")
                    Continue For
                End If

                Dim percentComplete As Single = intDBsProcessed / CSng(lstDatabaseNamesAndOutputPaths.Count) * 100

                UpdateProgress("Synchronizing database " & databaseName, percentComplete)

                Dim diSourceDirectory = New DirectoryInfo(schemaOutputDirectory)

                Dim targetDirectoryPath = String.Copy(directoryPathForSync)
                If lstDatabaseNamesAndOutputPaths.Count > 1 OrElse CreateDirectoryForEachDB Then
                    targetDirectoryPath = Path.Combine(targetDirectoryPath, databaseName)
                End If

                Dim diTargetDirectory = New DirectoryInfo(targetDirectoryPath)

                If Not diSourceDirectory.Exists Then
                    ShowErrorMessage("Source directory not found; cannot synchronize: " & diSourceDirectory.FullName)
                    Return False
                End If

                If Not diTargetDirectory.Exists Then
                    ShowMessage("Creating target directory for synchronization: " & diTargetDirectory.FullName)
                    diTargetDirectory.Create()
                End If

                If diSourceDirectory.FullName = diTargetDirectory.FullName Then
                    ShowErrorMessage("Sync directory is identical to the output SchemaFileDirectory; cannot synchronize")
                    Return False
                End If

                Dim fileProcessCount = 0
                Dim fileCopyCount = 0

                ' This list holds the the files that are copied from diSourceDirectory to diTargetDirectory
                Dim lstNewFilePaths = New List(Of String)

                Dim filesToCopy = diSourceDirectory.GetFiles()

                For Each sourceFile As FileInfo In filesToCopy

                    If sourceFile.Name.StartsWith("x_", StringComparison.OrdinalIgnoreCase) OrElse
                       sourceFile.Name.StartsWith("t_tmp_", StringComparison.OrdinalIgnoreCase) OrElse
                       sourceFile.Name.StartsWith("t_CandidateModsSeqWork_", StringComparison.OrdinalIgnoreCase) OrElse
                       sourceFile.Name.StartsWith("t_CandidateSeqWork_", StringComparison.OrdinalIgnoreCase) Then
                        ShowMessage("Skipping " & databaseName & " object " & sourceFile.Name)
                        Continue For
                    End If

                    Dim fiTargetFile = New FileInfo(Path.Combine(diTargetDirectory.FullName, sourceFile.Name))
                    Dim eDifferenceReason As eDifferenceReasonType

                    If FilesDiffer(sourceFile, fiTargetFile, eDifferenceReason) Then

                        Dim subtaskPercentComplete As Single = fileProcessCount / CSng(filesToCopy.Count) * 100

                        Select Case eDifferenceReason
                            Case eDifferenceReasonType.NewFile
                                UpdateSubtaskProgress("  Copying new file " & sourceFile.Name, subtaskPercentComplete)
                                lstNewFilePaths.Add(fiTargetFile.FullName)
                            Case eDifferenceReasonType.Changed
                                UpdateSubtaskProgress("  Copying changed file " & sourceFile.Name, subtaskPercentComplete)
                            Case Else
                                UpdateSubtaskProgress("  Copying file " & sourceFile.Name, subtaskPercentComplete)
                        End Select

                        sourceFile.CopyTo(fiTargetFile.FullName, True)
                        fileCopyCount += 1

                    End If

                    fileProcessCount += 1
                Next

                Dim commitMessageAppend = String.Empty
                If includeDbNameInCommitMessage Then
                    commitMessageAppend = databaseName
                End If

                If Me.SvnUpdate Then
                    UpdateRepoChanges(diTargetDirectory, fileCopyCount, lstNewFilePaths, eRepoManagerType.Svn, commitMessageAppend)
                End If

                If Me.HgUpdate Then
                    UpdateRepoChanges(diTargetDirectory, fileCopyCount, lstNewFilePaths, eRepoManagerType.Hg, commitMessageAppend)
                End If

                If Me.GitUpdate Then
                    UpdateRepoChanges(diTargetDirectory, fileCopyCount, lstNewFilePaths, eRepoManagerType.Git, commitMessageAppend)
                End If

                intDBsProcessed += 1
            Next

            If Me.ShowStats Then
                Console.WriteLine("Synchronized schema files in " & DateTime.UtcNow.Subtract(dtStartTime).TotalSeconds.ToString("0.0") & " seconds")
            End If

            Return True

        Catch ex As Exception
            HandleException("Error in SyncSchemaFiles", ex)
            Return False
        End Try

    End Function

    Private Function UpdateRepoChanges(
      diTargetDirectory As FileSystemInfo,
      fileCopyCount As Integer,
      lstNewFilePaths As ICollection(Of String),
      eRepoManager As eRepoManagerType,
      commitMessageAppend As String) As Boolean

        Const SVN_EXE_PATH = "C:\Program Files\TortoiseSVN\bin\svn.exe"
        Const SVN_SOURCE = "Installed with 64-bit Tortoise SVN, available at https://tortoisesvn.net/downloads.html"

        Const HG_EXE_PATH = "C:\Program Files\TortoiseHg\hg.exe"
        Const HG_SOURCE = "Installed with 64-bit Tortoise Hg, available at https://tortoisehg.bitbucket.io/download/index.html"

        Const GIT_EXE_PATH = "C:\Program Files\Git\bin\git.exe"
        Const GIT_SOURCE = "Installed with 64-bit Git for Windows, available at https://git-scm.com/download/win"

        Dim fiRepoExe As FileInfo
        Dim strRepoSource As String
        Dim strToolName = "Unknown"

        Try

            Select Case eRepoManager
                Case eRepoManagerType.Svn
                    fiRepoExe = New FileInfo(SVN_EXE_PATH)
                    strRepoSource = SVN_SOURCE
                    strToolName = "SVN"

                Case eRepoManagerType.Hg
                    fiRepoExe = New FileInfo(HG_EXE_PATH)
                    strRepoSource = HG_SOURCE
                    strToolName = "Hg"

                Case eRepoManagerType.Git
                    fiRepoExe = New FileInfo(GIT_EXE_PATH)
                    strRepoSource = GIT_SOURCE
                    strToolName = "Git"

                Case Else
                    ShowErrorMessage("Unsupported RepoManager type: " & eRepoManager.ToString())
                    Return False
            End Select

            If Not fiRepoExe.Exists Then
                ShowErrorMessage("Repo exe not found at " & fiRepoExe.FullName)
                ShowMessage(strRepoSource)
                Return False
            End If

            Dim cmdArgs As String
            Dim maxRuntimeSeconds As Integer
            Dim standardOutput = String.Empty
            Dim errorOutput = String.Empty
            Dim success As Boolean

            Console.WriteLine()
            If lstNewFilePaths.Count > 0 Then
                UpdateProgress("Adding " & lstNewFilePaths.Count & " new " & CheckPlural(lstNewFilePaths.Count, "file", "files") & " for tracking by " & strToolName)

                ' Add each of the new files
                For Each newFilePath In lstNewFilePaths
                    Dim fiNewFile = New FileInfo(newFilePath)

                    cmdArgs = " add """ & fiNewFile.FullName & """"
                    maxRuntimeSeconds = 30

                    success = RunCommand(fiRepoExe.FullName, cmdArgs, fiNewFile.Directory.FullName, standardOutput, errorOutput, maxRuntimeSeconds)

                    If Not success Then
                        ShowMessage("Error reported for " & strToolName & ": " & standardOutput)
                        Return False
                    ElseIf eRepoManager = eRepoManagerType.Git AndAlso errorOutput.StartsWith("fatal") Then
                        ShowMessage("Error reported for " & strToolName & ": " & errorOutput)
                    End If

                Next
                Console.WriteLine()
            End If

            UpdateProgress("Looking for modified files tracked by " & strToolName & " at " & diTargetDirectory.FullName)

            ' Count the number of new or modified files
            cmdArgs = " status """ & diTargetDirectory.FullName & """"
            maxRuntimeSeconds = 300
            If eRepoManager = eRepoManagerType.Git Then cmdArgs = " status -s -u"

            success = RunCommand(fiRepoExe.FullName, cmdArgs, diTargetDirectory.FullName, standardOutput, errorOutput, maxRuntimeSeconds)
            If Not success Then Return False
            If eRepoManager = eRepoManagerType.Git AndAlso errorOutput.StartsWith("fatal") Then
                ShowMessage("Error reported for " & strToolName & ": " & errorOutput)
            End If
            Console.WriteLine()

            Dim modifiedFileCount = 0

            If eRepoManager = eRepoManagerType.Svn Or eRepoManager = eRepoManagerType.Hg Then
                success = ParseSvnHgStatus(diTargetDirectory, standardOutput, eRepoManager, modifiedFileCount)
            Else
                ' Git
                success = ParseGitStatus(diTargetDirectory, standardOutput, modifiedFileCount)
            End If

            If Not success Then Return False

            If fileCopyCount > 0 And modifiedFileCount = 0 Then
                Console.WriteLine("Note: fileCopyCount is " & fileCopyCount & " yet the Modified File Count reported by " & strToolName & " is zero; this may indicate a problem")
                Console.WriteLine()
            End If

            If modifiedFileCount > 0 OrElse lstNewFilePaths.Count > 0 Then

                If modifiedFileCount > 0 Then
                    UpdateProgress("Found " & modifiedFileCount & " modified " & CheckPlural(modifiedFileCount, "file", "files"))
                End If

                Dim commitMessage = DateTime.Now.ToString("yyyy-MM-dd") & " auto-commit"
                If Not String.IsNullOrWhiteSpace(commitMessageAppend) Then
                    If commitMessageAppend.StartsWith(" ") Then
                        commitMessage &= commitMessageAppend
                    Else
                        commitMessage &= " " & commitMessageAppend
                    End If
                End If

                If Not Me.CommitUpdates Then
                    ShowMessage("Use /Commit to commit changes with commit message: " & commitMessage)
                Else
                    UpdateProgress("Commiting changes to " & strToolName & ": " & commitMessage)

                    ' Commit the changes
                    cmdArgs = " commit """ & diTargetDirectory.FullName & """ --message """ & commitMessage & """"
                    maxRuntimeSeconds = 120

                    success = RunCommand(fiRepoExe.FullName, cmdArgs, diTargetDirectory.FullName, standardOutput, errorOutput, maxRuntimeSeconds)

                    If Not success Then
                        ShowErrorMessage("Commit error" & ControlChars.NewLine & standardOutput)
                        Return False
                    ElseIf eRepoManager = eRepoManagerType.Git AndAlso errorOutput.StartsWith("fatal") Then
                        ShowMessage("Error reported for " & strToolName & ": " & errorOutput)
                        Return False
                    ElseIf eRepoManager = eRepoManagerType.Svn AndAlso errorOutput.Contains("Commit failed") Then
                        ShowMessage("Error reported for " & strToolName & ": " & errorOutput)
                        Return False
                    End If

                    If eRepoManager = eRepoManagerType.Hg Or eRepoManager = eRepoManagerType.Git Then

                        For iteration = 1 To 2
                            If eRepoManager = eRepoManagerType.Hg Then
                                ' Push the changes
                                cmdArgs = " push"
                            Else
                                ' Push the changes to the master, both on origin and GitHub
                                If iteration = 1 Then
                                    cmdArgs = " push origin"
                                Else
                                    cmdArgs = " push GitHub"
                                End If
                            End If

                            maxRuntimeSeconds = 300

                            success = RunCommand(fiRepoExe.FullName, cmdArgs, diTargetDirectory.FullName, standardOutput, errorOutput, maxRuntimeSeconds)

                            If eRepoManager = eRepoManagerType.Hg Then
                                Exit For
                            End If
                        Next

                    End If

                    Console.WriteLine()

                End If

            End If

            Return success

        Catch ex As Exception
            HandleException("Error in UpdateRepoChanges for tool " & strToolName, ex)
            Return False
        End Try

    End Function

    Private Sub UpdateSubtaskProgress(taskDescription As String, percentComplete As Single)
        Dim descriptionChanged = Not String.Equals(taskDescription, mSubtaskDescription)

        mSubtaskDescription = String.Copy(taskDescription)
        If percentComplete < 0 Then
            percentComplete = 0
        ElseIf percentComplete > 100 Then
            percentComplete = 100
        End If
        mSubtaskPercentComplete = percentComplete

        If descriptionChanged Then
            If mSubtaskPercentComplete < Single.Epsilon Then
                LogMessage(mSubtaskDescription.Replace(Environment.NewLine, "; "))
            Else
                LogMessage(mSubtaskDescription & " (" & mSubtaskPercentComplete.ToString("0.0") & "% complete)".Replace(Environment.NewLine, "; "))
            End If
        End If

        RaiseEvent SubtaskProgressChanged(taskDescription, percentComplete)

    End Sub

#Region "Event Handlers"

    Private Sub mDBSchemaExporter_ProgressChanged(taskDescription As String, percentComplete As Single) Handles mDBSchemaExporter.ProgressUpdate
        UpdateProgress(taskDescription, percentComplete)
    End Sub

    Private Sub mDBSchemaExporter_ProgressComplete() Handles mDBSchemaExporter.ProgressComplete
        OperationComplete()
    End Sub

    Private Sub mDBSchemaExporter_ProgressReset() Handles mDBSchemaExporter.ProgressReset
        ShowMessage(mDBSchemaExporter.ProgressStepDescription)
    End Sub

    Private Sub mDBSchemaExporter_SubtaskProgressChanged(taskDescription As String, percentComplete As Single) Handles mDBSchemaExporter.SubtaskProgressChanged
        UpdateSubtaskProgress("  " & taskDescription, percentComplete)
    End Sub

    Private Sub mDBSchemaExporter_SubtaskProgressReset() Handles mDBSchemaExporter.SubtaskProgressReset
        ShowMessage("  " & mDBSchemaExporter.SubtaskProgressStepDescription)
    End Sub

#End Region

End Class
