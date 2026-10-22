namespace VamSys.Infrastructure;

// Named points inside storage operations at which tests may inject a failure or pause. The implementation lives
// in tests/TestSupport and is compiled only with VamSysTestSupport=true.
internal interface IStorageFaults
{
    void Reached(string point);
}
