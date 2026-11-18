using NUnit.Framework;

// A fresh instance of the test class for every test, so a constructor, a field and Dispose mean
// "per test" — NUnit's default is one instance shared by the whole fixture. Fixtures still run one
// at a time: ToolErrorMappingTests and ToolBehaviourTests both change ToolErrors' process-wide state.
[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
