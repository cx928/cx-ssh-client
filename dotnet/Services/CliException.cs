namespace CxSshClient.Cli.Services;

/// <summary>面向用户的错误：消息已是友好中文，命令层直接打印，不输出堆栈。</summary>
public class CliException : Exception
{
    public CliException(string message) : base(message) { }
    public CliException(string message, Exception inner) : base(message, inner) { }
}
