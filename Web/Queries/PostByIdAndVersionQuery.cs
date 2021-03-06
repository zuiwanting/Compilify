﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Compilify.LanguageServices;
using Compilify.Models;
using Compilify.Web.Models;

namespace Compilify.Web.Queries
{
    public class PostByIdAndVersionQuery : IQuery
    {
        private readonly IPostRepository posts;
        private readonly ICodeValidator validator;

        public PostByIdAndVersionQuery(IPostRepository postRepository, ICodeValidator codeValidator)
        {
            posts = postRepository;
            validator = codeValidator;
        }

        public Task<PostViewModel> Execute(string slug, int version)
        {
            var post = posts.GetVersion(slug, version);

            if (post == null)
            {
                return Task.FromResult<PostViewModel>(null);
            }

            var errors = GetErrorsInPost(post);

            var viewModel = PostViewModel.Create(post);

            viewModel.Errors = errors;

            return Task.FromResult(viewModel);
        }

        private IEnumerable<EditorError> GetErrorsInPost(ICodeProject post)
        {
            return validator.GetCompilationErrors(post);
        }
    }
}
