package api

import "context"

type Library struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	MediaType string `json:"mediaType"`
}

// Catalog is the slice of the host's library Moonbase reads.
type Catalog interface {
	// Libraries lists the libraries the caller may see.
	Libraries(ctx context.Context, caller Identity) ([]Library, error)
}
