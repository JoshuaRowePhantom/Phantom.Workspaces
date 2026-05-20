namespace Phantom.Workspaces.Llm;

public interface IMcpServer
{
    string GetDescription();

    IAgentExecutionEnvironment GetAgentExecutionEnvironment();
}
