package codexcw

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"strings"
	"testing"

	"github.com/stretchr/testify/require"
)

func TestGodocCoverage(t *testing.T) {
	files := parsePackageFiles(t)

	hasPackageDoc := false
	for _, file := range files {
		hasPackageDoc = hasPackageDoc || file.Doc != nil && strings.TrimSpace(file.Doc.Text()) != ""
	}
	require.True(t, hasPackageDoc, "package codexcw must have package documentation")

	for path, file := range files {
		for _, decl := range file.Decls {
			switch decl := decl.(type) {
			case *ast.GenDecl:
				checkGenDeclDocs(t, path, decl)
			case *ast.FuncDecl:
				if decl.Name.IsExported() && receiverExported(decl) {
					require.NotNil(t, decl.Doc, "%s exported function or method %s lacks godoc", path, decl.Name.Name)
				}
			}
		}
	}
}

func parsePackageFiles(t *testing.T) map[string]*ast.File {
	t.Helper()

	entries, err := os.ReadDir(".")
	require.NoError(t, err)

	files := map[string]*ast.File{}
	fset := token.NewFileSet()
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasSuffix(name, ".go") || strings.HasSuffix(name, "_test.go") {
			continue
		}
		file, err := parser.ParseFile(fset, name, nil, parser.ParseComments)
		require.NoError(t, err)
		if file.Name.Name == "codexcw" {
			files[name] = file
		}
	}
	return files
}

func checkGenDeclDocs(t *testing.T, path string, decl *ast.GenDecl) {
	t.Helper()

	for _, spec := range decl.Specs {
		switch spec := spec.(type) {
		case *ast.TypeSpec:
			if spec.Name.IsExported() {
				require.True(t, hasDoc(decl.Doc, spec.Doc), "%s exported type %s lacks godoc", path, spec.Name.Name)
				checkTypeFieldsDocs(t, path, spec.Name.Name, spec.Type)
			}
		case *ast.ValueSpec:
			for _, name := range spec.Names {
				if name.IsExported() {
					require.True(t, hasDoc(decl.Doc, spec.Doc), "%s exported value %s lacks godoc", path, name.Name)
				}
			}
		}
	}
}

func checkTypeFieldsDocs(t *testing.T, path string, owner string, expr ast.Expr) {
	t.Helper()

	switch typ := expr.(type) {
	case *ast.StructType:
		for _, field := range typ.Fields.List {
			for _, name := range field.Names {
				if name.IsExported() {
					require.NotNil(t, field.Doc, "%s exported field %s.%s lacks godoc", path, owner, name.Name)
				}
			}
		}
	case *ast.InterfaceType:
		for _, method := range typ.Methods.List {
			for _, name := range method.Names {
				if name.IsExported() {
					require.NotNil(t, method.Doc, "%s exported interface method %s.%s lacks godoc", path, owner, name.Name)
				}
			}
		}
	}
}

func hasDoc(groups ...*ast.CommentGroup) bool {
	for _, group := range groups {
		if group != nil && strings.TrimSpace(group.Text()) != "" {
			return true
		}
	}
	return false
}

func receiverExported(decl *ast.FuncDecl) bool {
	if decl.Recv == nil || len(decl.Recv.List) == 0 {
		return true
	}

	switch expr := decl.Recv.List[0].Type.(type) {
	case *ast.Ident:
		return expr.IsExported()
	case *ast.StarExpr:
		ident, ok := expr.X.(*ast.Ident)
		return ok && ident.IsExported()
	default:
		return false
	}
}
