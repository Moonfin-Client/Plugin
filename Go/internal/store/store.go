// Package store keeps Moonbase's own data. Each host supplies a KV: Silo's
// per-installation instance state, or files on disk for the Plex companion.
package store

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"hash/fnv"
	"io/fs"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"sync"
)

// Silo's instance state limits. MemKV enforces them so tests catch code that
// would break on Silo, and the silo package checks them against the SDK.
const (
	MaxKeys       = 256
	MaxValueBytes = 256 << 10
)

// KV is the storage Moonbase needs: whole values under short keys.
type KV interface {
	Get(ctx context.Context, key string) ([]byte, bool, error)
	Put(ctx context.Context, key string, value []byte) error
}

// MemKV is an in-memory KV with Silo's limits, for tests.
type MemKV struct {
	mu   sync.Mutex
	data map[string][]byte
}

func NewMemKV() *MemKV { return &MemKV{data: map[string][]byte{}} }

func (m *MemKV) Get(_ context.Context, key string) ([]byte, bool, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	v, ok := m.data[key]
	return append([]byte(nil), v...), ok, nil
}

func (m *MemKV) Put(_ context.Context, key string, value []byte) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(value) > MaxValueBytes {
		return fmt.Errorf("value for %q is %d bytes, limit %d", key, len(value), MaxValueBytes)
	}
	if _, ok := m.data[key]; !ok && len(m.data) >= MaxKeys {
		return fmt.Errorf("instance state is full (%d keys)", MaxKeys)
	}
	m.data[key] = append([]byte(nil), value...)
	return nil
}

// FileKV keeps one file per key in a directory.
type FileKV struct{ Dir string }

func (f FileKV) path(key string) string {
	return filepath.Join(f.Dir, url.PathEscape(key)+".json")
}

func (f FileKV) Get(_ context.Context, key string) ([]byte, bool, error) {
	b, err := os.ReadFile(f.path(key))
	if errors.Is(err, fs.ErrNotExist) {
		return nil, false, nil
	}
	return b, err == nil, err
}

// Put writes through a temporary file so a crash never leaves half a value.
func (f FileKV) Put(_ context.Context, key string, value []byte) error {
	tmp, err := os.CreateTemp(f.Dir, ".put-*")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.Write(value); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(tmp.Name(), f.path(key))
}

// PackedBuckets is how many keys Packed spreads its values over. It's part of
// the stored layout: changing it would lose track of every packed value.
const PackedBuckets = 32

// Packed keeps many small values under a few gzipped keys, so they don't each
// use up one of Silo's 256 instance-state keys. Values for keys Pack accepts
// share PackedBuckets keys ("pk/<n>", chosen by a hash of the key); every
// other key goes straight to KV. An empty value deletes, which frees its room
// in the bucket: Silo can't delete a key, only empty it.
//
// Values written before packing stay under their own key until written
// again: a read falls back to that key, and a write moves the value into its
// bucket and empties the old key.
type Packed struct {
	KV   KV
	Pack func(key string) bool
	// Serializes the read-modify-write of a bucket. Silo runs one plugin
	// process per instance-state scope.
	mu sync.Mutex
}

func packedBucket(key string) string {
	h := fnv.New32a()
	_, _ = h.Write([]byte(key))
	return "pk/" + strconv.Itoa(int(h.Sum32()%PackedBuckets))
}

func (p *Packed) readBucket(ctx context.Context, bucket string) (map[string]json.RawMessage, error) {
	values := map[string]json.RawMessage{}
	raw, found, err := p.KV.Get(ctx, bucket)
	if err != nil || !found || len(raw) == 0 {
		return values, err
	}
	zr, err := gzip.NewReader(bytes.NewReader(raw))
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", bucket, err)
	}
	if err := json.NewDecoder(zr).Decode(&values); err != nil {
		return nil, fmt.Errorf("read %s: %w", bucket, err)
	}
	return values, nil
}

func (p *Packed) Get(ctx context.Context, key string) ([]byte, bool, error) {
	values, err := p.getMany(ctx, []string{key})
	v, found := values[key]
	return v, found, err
}

func (p *Packed) getMany(ctx context.Context, keys []string) (map[string][]byte, error) {
	out := map[string][]byte{}
	buckets := map[string]map[string]json.RawMessage{}
	for _, key := range keys {
		if p.Pack(key) {
			bucket := packedBucket(key)
			values, ok := buckets[bucket]
			if !ok {
				var err error
				p.mu.Lock()
				values, err = p.readBucket(ctx, bucket)
				p.mu.Unlock()
				if err != nil {
					return nil, err
				}
				buckets[bucket] = values
			}
			if v, ok := values[key]; ok {
				out[key] = v
				continue
			}
		}
		v, found, err := p.KV.Get(ctx, key)
		if err != nil {
			return nil, err
		}
		if found {
			out[key] = v
		}
	}
	return out, nil
}

func (p *Packed) Put(ctx context.Context, key string, value []byte) error {
	_, err := p.putMany(ctx, map[string][]byte{key: value})
	return err
}

func (p *Packed) putMany(ctx context.Context, values map[string][]byte) ([]string, error) {
	var failed []string
	var first error
	fail := func(err error, keys ...string) {
		failed = append(failed, keys...)
		if first == nil {
			first = err
		}
	}
	byBucket := map[string][]string{}
	for key, value := range values {
		switch {
		case !p.Pack(key):
			if err := p.KV.Put(ctx, key, value); err != nil {
				fail(err, key)
			}
		case len(value) > 0 && !json.Valid(value):
			fail(fmt.Errorf("value for %q isn't JSON", key), key)
		default:
			bucket := packedBucket(key)
			byBucket[bucket] = append(byBucket[bucket], key)
		}
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	for bucket, keys := range byBucket {
		if err := p.writeBucket(ctx, bucket, keys, values); err != nil {
			fail(err, keys...)
			continue
		}
		// The values are saved even if an old key stays behind.
		if err := p.clearOldKeys(ctx, keys); err != nil && first == nil {
			first = err
		}
	}
	return failed, first
}

// writeBucket stores values' keys, all in bucket, with one read and write of
// it. The caller holds p.mu.
func (p *Packed) writeBucket(ctx context.Context, bucket string, keys []string, values map[string][]byte) error {
	stored, err := p.readBucket(ctx, bucket)
	if err != nil {
		return err
	}
	for _, key := range keys {
		if len(values[key]) == 0 {
			delete(stored, key)
		} else {
			stored[key] = values[key]
		}
	}
	var buf bytes.Buffer
	zw := gzip.NewWriter(&buf)
	if err := json.NewEncoder(zw).Encode(stored); err != nil {
		return err
	}
	if err := zw.Close(); err != nil {
		return err
	}
	if buf.Len() > MaxValueBytes {
		return fmt.Errorf("%s would be %d bytes, limit %d", bucket, buf.Len(), MaxValueBytes)
	}
	return p.KV.Put(ctx, bucket, buf.Bytes())
}

// clearOldKeys empties keys' values from before packing, which would
// otherwise come back once the packed ones are deleted. Writing an existing
// key never needs a free one. A key it can't clear is tried again on the
// value's next write.
func (p *Packed) clearOldKeys(ctx context.Context, keys []string) error {
	var errs []error
	for _, key := range keys {
		old, found, err := p.KV.Get(ctx, key)
		if err == nil && found && len(old) > 0 {
			err = p.KV.Put(ctx, key, nil)
		}
		if err != nil {
			errs = append(errs, fmt.Errorf("clear old %s: %w", key, err))
		}
	}
	return errors.Join(errs...)
}

// GetMany reads keys from kv, leaving out those not found. A Packed reads
// each bucket once rather than once per key.
func GetMany(ctx context.Context, kv KV, keys []string) (map[string][]byte, error) {
	if p, ok := kv.(*Packed); ok {
		return p.getMany(ctx, keys)
	}
	out := map[string][]byte{}
	for _, key := range keys {
		v, found, err := kv.Get(ctx, key)
		if err != nil {
			return nil, err
		}
		if found {
			out[key] = v
		}
	}
	return out, nil
}

// PutMany writes values to kv, an empty value deleting its key. A Packed
// reads and writes each bucket once rather than once per key. It carries on
// past a failed write and returns the keys it didn't write and the first
// error. The error can also be about a written value's old unpacked key,
// which wasn't cleared; that key isn't in the list.
func PutMany(ctx context.Context, kv KV, values map[string][]byte) ([]string, error) {
	if p, ok := kv.(*Packed); ok {
		return p.putMany(ctx, values)
	}
	var failed []string
	var first error
	for key, value := range values {
		if err := kv.Put(ctx, key, value); err != nil {
			failed = append(failed, key)
			if first == nil {
				first = err
			}
		}
	}
	return failed, first
}
