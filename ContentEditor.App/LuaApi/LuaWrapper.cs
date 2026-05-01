using System.Text;
using ContentEditor.App.Windowing;
using ContentPatcher;
using Lua;
using Lua.Internal;
using Lua.IO;
using Lua.Platforms;
using Lua.Standard;

namespace ContentEditor.App.Lua;

public class LuaWrapper
{
    private LuaState state = LuaState.Create(new LuaPlatform(new FileSystem(), new SystemOsEnvironment(), new LuaIO(), System.TimeProvider.System));

    public static LuaWrapper Create(ContentWorkspace workspace, EditorWindow? window)
    {
        var wrapper = new LuaWrapper();
        wrapper.state.OpenBasicLibrary();
        wrapper.state.OpenBitwiseLibrary();
        wrapper.state.OpenMathLibrary();
        // wrapper.state.OpenModuleLibrary(); // enable modules once we implement proper resolution of user lua path files
        wrapper.state.OpenStringLibrary();
        wrapper.state.OpenTableLibrary();
        wrapper.state.Environment["json"] = new LuaJson();
        wrapper.state.Environment["print"] = new LuaLog().LogFunction;
        wrapper.state.Environment["env"] = new LuaWorkspaceWrapper(workspace);
        if (window != null) {
            wrapper.state.Environment["window"] = new LuaWindowWrapper(window);
        }
        return wrapper;
    }

    public void Run(string script)
    {
        var tokenSource = new CancellationTokenSource();
        var result = state.DoStringAsync(script, null, tokenSource.Token);
        if (result.IsCompletedSuccessfully) {
            if (result.Result.Length == 0) {
                Logger.Info("Script finished");
            } else if (result.Result.Length == 1) {
                Logger.Info("Script result: " + LuaJson.LuaToString(result.Result[0]));
            } else {
                Logger.Info("Results:");
                foreach (var res in result.Result) {
                    Logger.Info(LuaJson.LuaToString(res));
                }
            }
        } else if (result.IsFaulted) {
            var asTask = result.AsTask();
            if (asTask.Exception != null) {
                Logger.Error(asTask.Exception, "Script failed");
            } else {
                Logger.Error("Script failed for unknown reasons");
            }
        } else if (!result.IsCompleted) {
            // async is not supported for now, force cancel it
            tokenSource.Cancel();
            Logger.Warn("The script did not finish immediately, async is not yet supported");
        } else {
            Logger.Info("No idea what happened there, sorry.");
        }
    }

    private sealed class LuaIO : ILuaStandardIO
    {
        private ConsoleStandardIO _defaultIO = new ConsoleStandardIO();

        private ConsoleOutput? standardOutput;

        private ConsoleOutput? standardError;

        public ILuaStream Input => _defaultIO.Input;

        public ILuaStream Output => standardOutput ?? (standardOutput = new ConsoleOutput(LogSeverity.Info));

        public ILuaStream Error => standardError ?? (standardError = new ConsoleOutput(LogSeverity.Error));

        private class ConsoleOutput(LogSeverity level) : ILuaStream
        {
            public bool IsOpen => true;

            public LuaFileOpenMode Mode => LuaFileOpenMode.Append;

            public void Dispose()
            {
            }

            private StringBuilder buffer = new();

            ValueTask ILuaStream.WriteAsync(ReadOnlyMemory<char> content, CancellationToken cancellationToken)
            {
                buffer.Append(content);
                return default(ValueTask);
            }

            ValueTask ILuaStream.WriteAsync(string content, CancellationToken cancellationToken)
            {
                buffer.Append(content);
                return default(ValueTask);
            }


            ValueTask ILuaStream.FlushAsync(CancellationToken cancellationToken)
            {
                Logger.Log(level, buffer.ToString());
                buffer.Clear();
                return default(ValueTask);
            }
        }
    }
}