// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Shared;

using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Evaluates a function expression, such as "Exists('foo')"
    /// </summary>
    internal sealed class FunctionCallExpressionNode : OperatorExpressionNode
    {
        private readonly List<GenericExpressionNode> _arguments;
        private readonly string _functionName;

        /// <summary>
        /// Environment variable to enable diagnostic logging for Exists() condition evaluation.
        /// Set MSBUILD_EXISTS_DIAGNOSTIC_LOG to a file path to capture diagnostics.
        /// Investigating: https://github.com/dotnet/msbuild/issues/13420
        /// </summary>
        private static readonly string s_diagnosticLogFile = Environment.GetEnvironmentVariable("MSBUILD_EXISTS_DIAGNOSTIC_LOG");
        private static readonly bool s_diagnosticEnabled = !string.IsNullOrEmpty(s_diagnosticLogFile);

        internal FunctionCallExpressionNode(string functionName, List<GenericExpressionNode> arguments)
        {
            _functionName = functionName;
            _arguments = arguments;
        }

        /// <summary>
        /// Evaluate node as boolean
        /// </summary>
        internal override bool BoolEvaluate(ConditionEvaluator.IConditionEvaluationState state)
        {
            if (String.Equals(_functionName, "exists", StringComparison.OrdinalIgnoreCase))
            {
                // Check we only have one argument
                VerifyArgumentCount(1, state);

                try
                {
                    // Expand the items and use DefaultIfEmpty in case there is nothing returned
                    // Then check if everything is not null (because the list was empty), not
                    // already loaded into the cache, and exists
                    List<string> list = ExpandArgumentAsFileList(_arguments[0], state);
                    if (list == null)
                    {
                        return false;
                    }

                    foreach (var item in list)
                    {
                        if (item == null)
                        {
                            return false;
                        }

                        bool foundInCache = state.LoadedProjectsCache?.TryGet(item) != null;
                        if (foundInCache)
                        {
                            continue;
                        }

                        bool existsOnDisk = FileUtilities.FileOrDirectoryExistsNoThrow(item, state.FileSystem);
                        if (!existsOnDisk)
                        {
                            if (s_diagnosticEnabled)
                            {
                                // Theory 1: Transient OS error — re-check with uncached File.Exists/Directory.Exists
                                bool recheckFile = File.Exists(item);
                                bool recheckDir = Directory.Exists(item);
                                string parentDir = Path.GetDirectoryName(item);
                                bool parentExists = parentDir != null && Directory.Exists(parentDir);

                                // Theory 2: Wrong path — log the exact expanded path
                                string unexpandedArg = _arguments[0].GetUnexpandedValue(state);

                                // Theory 3: Cache poisoning — log whether PRE cache was checked
                                bool preChecked = state.LoadedProjectsCache != null;

                                DiagnosticLog(
                                    "[{0:O}] Exists() returned FALSE for \"{1}\". " +
                                    "Unexpanded=\"{2}\". " +
                                    "Uncached re-check: File.Exists={3}, Directory.Exists={4}, ParentDirExists={5}. " +
                                    "LoadedProjectsCache checked={6}. " +
                                    "EvalDir=\"{7}\", Location={8}, Condition=\"{9}\"",
                                    DateTime.UtcNow,
                                    item,
                                    unexpandedArg,
                                    recheckFile,
                                    recheckDir,
                                    parentExists,
                                    preChecked,
                                    state.EvaluationDirectory ?? "(null)",
                                    state.ElementLocation?.LocationString ?? "(null)",
                                    state.Condition ?? "(null)");

                                if (recheckFile || recheckDir)
                                {
                                    // File exists NOW but Exists() said it didn't — this is the smoking gun
                                    DiagnosticLog(
                                        "[{0:O}] *** MISMATCH DETECTED *** File/dir exists on uncached re-check but FileOrDirectoryExistsNoThrow returned false! " +
                                        "Path=\"{1}\". This confirms a transient failure in the filesystem layer.",
                                        DateTime.UtcNow,
                                        item);
                                }
                                else
                                {
                                    // File genuinely not visible — list the parent directory contents for comparison
                                    if (parentExists)
                                    {
                                        try
                                        {
                                            string[] files = Directory.GetFiles(parentDir);
                                            DiagnosticLog(
                                                "[{0:O}] Parent directory \"{1}\" contains {2} files: [{3}]",
                                                DateTime.UtcNow,
                                                parentDir,
                                                files.Length,
                                                string.Join(", ", files));
                                        }
                                        catch (Exception dirEx)
                                        {
                                            DiagnosticLog(
                                                "[{0:O}] Failed to list parent directory \"{1}\": {2}: {3}",
                                                DateTime.UtcNow,
                                                parentDir,
                                                dirEx.GetType().Name,
                                                dirEx.Message);
                                        }
                                    }
                                }
                            }

                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    // Ignore invalid characters or path related exceptions

                    // We will ignore the PathTooLong exception caused by GetFullPath because in single proc this code
                    // is not executed and the condition is just evaluated to false as File.Exists and Directory.Exists does not throw in this situation.
                    // To be consistant with that we will return a false in this case also.
                    // DevDiv Bugs: 46035

                    if (s_diagnosticEnabled)
                    {
                        string unexpandedArg = _arguments.Count > 0 ? _arguments[0].GetUnexpandedValue(state) : "(no args)";
                        string expandedPath = "(expansion failed)";
                        try
                        {
                            var expandedList = ExpandArgumentAsFileList(_arguments[0], state);
                            if (expandedList?.Count > 0)
                            {
                                expandedPath = expandedList[0];
                            }
                        }
                        catch { /* ignore — we're already in error handling */ }

                        DiagnosticLog(
                            "[{0:O}] *** EXCEPTION SWALLOWED *** Exists() caught {1} and returned false. " +
                            "Unexpanded=\"{2}\", Expanded=\"{3}\", " +
                            "Location={4}, Condition=\"{5}\". " +
                            "Exception: {6}",
                            DateTime.UtcNow,
                            e.GetType().FullName,
                            unexpandedArg,
                            expandedPath,
                            state.ElementLocation?.LocationString ?? "(null)",
                            state.Condition ?? "(null)",
                            e.ToString());
                    }

                    return false;
                }
            }
            else if (String.Equals(_functionName, "HasTrailingSlash", StringComparison.OrdinalIgnoreCase))
            {
                // Check we only have one argument
                VerifyArgumentCount(1, state);

                // Expand properties and items, and verify the result is an appropriate scalar
                string expandedValue = ExpandArgumentForScalarParameter("HasTrailingSlash", _arguments[0], state);

                // Is the last character a backslash?
                if (expandedValue.Length != 0)
                {
                    char lastCharacter = expandedValue[expandedValue.Length - 1];
                    // Either back or forward slashes satisfy the function: this is useful for URL's
                    return lastCharacter == Path.DirectorySeparatorChar || lastCharacter == Path.AltDirectorySeparatorChar || lastCharacter == '\\';
                }
                else
                {
                    return false;
                }
            }
            // We haven't implemented any other "functions"
            else
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    state.ElementLocation,
                    "UndefinedFunctionCall",
                    state.Condition,
                    _functionName);

                return false;
            }
        }

        /// <summary>
        /// Diagnostic logging for https://github.com/dotnet/msbuild/issues/13420.
        /// Writes to the file specified by MSBUILD_EXISTS_DIAGNOSTIC_LOG env var and to stderr.
        /// </summary>
        private static void DiagnosticLog(string format, params object[] args)
        {
            try
            {
                string message = "MSB13420 diagnostic: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);

                if (!string.IsNullOrEmpty(s_diagnosticLogFile))
                {
                    File.AppendAllText(s_diagnosticLogFile, message + Environment.NewLine);
                }

                Console.Error.WriteLine(message);
            }
            catch
            {
                // Don't let diagnostic logging cause build failures
            }
        }

        /// <summary>
        /// Expands properties and items in the argument, and verifies that the result is consistent
        /// with a scalar parameter type.
        /// </summary>
        /// <param name="function">Function name for errors</param>
        /// <param name="argumentNode">Argument to be expanded</param>
        /// <param name="state"></param>
        /// <param name="isFilePath">True if this is afile name and the path should be normalized</param>
        /// <returns>Scalar result</returns>
        private static string ExpandArgumentForScalarParameter(string function, GenericExpressionNode argumentNode, ConditionEvaluator.IConditionEvaluationState state,
            bool isFilePath = true)
        {
            string argument = argumentNode.GetUnexpandedValue(state);

            // Fix path before expansion
            if (isFilePath)
            {
                argument = FileUtilities.FixFilePath(argument);
            }

            IList<TaskItem> items = state.ExpandIntoTaskItems(argument);

            string expandedValue = String.Empty;

            if (items.Count == 0)
            {
                // Empty argument, that's fine.
            }
            else if (items.Count == 1)
            {
                expandedValue = items[0].ItemSpec;
            }
            else // too many items for the function
            {
                // We only allow a single item to be passed into a scalar parameter.
                ProjectErrorUtilities.ThrowInvalidProject(
                    state.ElementLocation,
                    "CannotPassMultipleItemsIntoScalarFunction", function, argument,
                    state.ExpandIntoString(argument));
            }

            return expandedValue;
        }

        private List<string> ExpandArgumentAsFileList(GenericExpressionNode argumentNode, ConditionEvaluator.IConditionEvaluationState state, bool isFilePath = true)
        {
            string argument = argumentNode.GetUnexpandedValue(state);

            // Fix path before expansion
            if (isFilePath)
            {
                argument = FileUtilities.FixFilePath(argument);
            }

            IList<TaskItem> expanded = state.ExpandIntoTaskItems(argument);
            var expandedCount = expanded.Count;

            if (expandedCount == 0)
            {
                return null;
            }

            var list = new List<string>(capacity: expandedCount);
            for (var i = 0; i < expandedCount; i++)
            {
                var item = expanded[i];
                if (state.EvaluationDirectory != null && !Path.IsPathRooted(item.ItemSpec))
                {
                    list.Add(Path.GetFullPath(Path.Combine(state.EvaluationDirectory, item.ItemSpec)));
                }
                else
                {
                    list.Add(item.ItemSpec);
                }
            }

            return list;
        }

        /// <summary>
        /// Check that the number of function arguments is correct.
        /// </summary>
        private void VerifyArgumentCount(int expected, ConditionEvaluator.IConditionEvaluationState state)
        {
            ProjectErrorUtilities.VerifyThrowInvalidProject(
                _arguments.Count == expected,
                 state.ElementLocation,
                 "IncorrectNumberOfFunctionArguments",
                 state.Condition,
                 _arguments.Count,
                 expected);
        }
    }
}
