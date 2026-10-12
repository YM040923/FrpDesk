package process

import (
	"path/filepath"
	"testing"
)

func TestDesiredStatePersists(t *testing.T) {
	state := filepath.Join(t.TempDir(), "active-instances.json")
	m := New("", t.TempDir(), t.TempDir(), state)
	m.mu.Lock()
	if err := m.setDesiredLocked("profile-1", true); err != nil {
		t.Fatal(err)
	}
	m.mu.Unlock()

	reloaded := New("", t.TempDir(), t.TempDir(), state)
	if !reloaded.WantsStart("profile-1") {
		t.Fatal("active instance state was not restored")
	}

	reloaded.mu.Lock()
	if err := reloaded.setDesiredLocked("profile-1", false); err != nil {
		t.Fatal(err)
	}
	reloaded.mu.Unlock()
	if New("", t.TempDir(), t.TempDir(), state).WantsStart("profile-1") {
		t.Fatal("stopped instance state was not cleared")
	}
}
